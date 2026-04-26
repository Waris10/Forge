using Forge.Storage.Redis;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Forge.Scheduler;

/// <summary>
/// The scheduler's main loop. At any moment exactly one PromotionService
/// instance across the fleet holds the lock and runs the promote loop;
/// the others idle and retry acquiring it.
///
/// Lifecycle of a single instance:
///   1. Try to acquire the singleton lock.
///   2a. Got it: become the leader. Loop {promote due jobs, refresh lock,
///       sleep tick interval}. If a refresh ever fails, we've lost the
///       lock (TTL expired during a stall) — drop back to step 1.
///   2b. Didn't get it: another instance is leading. Sleep, retry from 1.
///
/// On graceful shutdown: release the lock if we hold it, so a peer can
/// take over immediately rather than waiting out the TTL.
/// </summary>
public class PromotionService : BackgroundService
{
    private const string LockKey = "forge:lock:scheduler";

    private readonly RedisDistributedLock _lock;
    private readonly IJobQueue _queue;
    private readonly SchedulerOptions _options;
    private readonly ILogger<PromotionService> _logger;

    public PromotionService(
        RedisDistributedLock distributedLock,
        IJobQueue queue,
        IOptions<SchedulerOptions> options,
        ILogger<PromotionService> logger)
    {
        _lock = distributedLock;
        _queue = queue;
        _options = options.Value;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.LogInformation(
            "Scheduler started. InstanceId={InstanceId} LockTtl={LockTtl} TickInterval={TickInterval}",
            _options.InstanceId, _options.LockTtl, _options.TickInterval);

        try
        {
            while (!stoppingToken.IsCancellationRequested)
            {
                var acquired = await _lock.TryAcquire(
                    LockKey, _options.InstanceId, _options.LockTtl);

                if (!acquired)
                {
                    _logger.LogDebug("Lock held by another instance. Retrying in {Interval}.",
                        _options.LockAcquireRetryInterval);
                    await SafeDelay(_options.LockAcquireRetryInterval, stoppingToken);
                    continue;
                }

                _logger.LogInformation("Acquired scheduler lock. Becoming leader.");

                try
                {
                    await RunAsLeader(stoppingToken);
                }
                finally
                {
                    // Try to release on the way out, regardless of why we
                    // exited the leader loop. If we lost the lock and someone
                    // else has it, the script-side check makes Release a no-op.
                    await _lock.Release(LockKey, _options.InstanceId);
                    _logger.LogInformation("Released scheduler lock.");
                }
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            // Normal shutdown.
        }

        _logger.LogInformation("Scheduler stopped.");
    }

    private async Task RunAsLeader(CancellationToken stoppingToken)
    {
        var lastRefresh = DateTimeOffset.UtcNow;

        while (!stoppingToken.IsCancellationRequested)
        {
            // 1. Promote due jobs.
            try
            {
                var promoted = await _queue.PromoteDueJobs(_options.BatchSize, stoppingToken);
                if (promoted > 0)
                {
                    _logger.LogInformation("Promoted {Count} due jobs.", promoted);
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Promote tick errored. Continuing.");
                // Don't drop the lock for a transient error — keep leadership,
                // try again next tick.
            }

            // 2. Refresh the lock if it's been a while.
            if (DateTimeOffset.UtcNow - lastRefresh >= _options.LockRefreshInterval)
            {
                var refreshed = await _lock.TryRefresh(
                    LockKey, _options.InstanceId, _options.LockTtl);

                if (!refreshed)
                {
                    _logger.LogWarning(
                        "Lock refresh failed — lost leadership. Dropping back to acquire loop.");
                    return;  // back to the outer acquire loop
                }

                lastRefresh = DateTimeOffset.UtcNow;
                _logger.LogDebug("Lock refreshed.");
            }

            // 3. Sleep until next tick.
            await SafeDelay(_options.TickInterval, stoppingToken);
        }
    }

    /// <summary>
    /// Task.Delay that swallows the OperationCanceledException on shutdown.
    /// Lets the caller fall through naturally on cancellation rather than
    /// having to write try/catch around every Delay.
    /// </summary>
    private static async Task SafeDelay(TimeSpan delay, CancellationToken ct)
    {
        try { await Task.Delay(delay, ct); }
        catch (OperationCanceledException) { /* graceful shutdown */ }
    }
}