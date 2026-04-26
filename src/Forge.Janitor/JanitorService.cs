using Forge.Storage.Redis;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Forge.Janitor;

/// <summary>
/// The janitor's main loop. Singleton via Redis lock — same pattern as
/// PromotionService.
///
/// While leader: every <see cref="JanitorOptions.ScanInterval"/>, scan
/// Redis for processing lists whose corresponding heartbeat key is gone.
/// For each dead worker, call RecoverDeadWorker — atomic per-job:
/// requeue if under the threshold, DLQ if over.
/// </summary>
public class JanitorService : BackgroundService
{
    private const string LockKey = "forge:lock:janitor";

    private readonly RedisDistributedLock _lock;
    private readonly IJobQueue _queue;
    private readonly JanitorOptions _options;
    private readonly ILogger<JanitorService> _logger;

    public JanitorService(
        RedisDistributedLock distributedLock,
        IJobQueue queue,
        IOptions<JanitorOptions> options,
        ILogger<JanitorService> logger)
    {
        _lock = distributedLock;
        _queue = queue;
        _options = options.Value;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.LogInformation(
            "Janitor started. InstanceId={InstanceId} ScanInterval={ScanInterval} MaxRequeue={MaxRequeue}",
            _options.InstanceId, _options.ScanInterval, _options.MaxRequeueCount);

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

                _logger.LogInformation("Acquired janitor lock. Becoming leader.");

                try
                {
                    await RunAsLeader(stoppingToken);
                }
                finally
                {
                    await _lock.Release(LockKey, _options.InstanceId);
                    _logger.LogInformation("Released janitor lock.");
                }
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            // Normal shutdown.
        }

        _logger.LogInformation("Janitor stopped.");
    }

    private async Task RunAsLeader(CancellationToken stoppingToken)
    {
        var lastRefresh = DateTimeOffset.UtcNow;

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                var dead = await _queue.FindDeadWorkers(stoppingToken);

                foreach (var workerId in dead)
                {
                    var (recovered, poisoned) = await _queue.RecoverDeadWorker(
                        workerId, _options.MaxRequeueCount, stoppingToken);

                    _logger.LogWarning(
                        "Recovered dead worker {WorkerId}: {Recovered} requeued, {Poisoned} sent to DLQ.",
                        workerId, recovered, poisoned);
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Janitor scan errored. Continuing.");
            }

            // Refresh the lock if interval has passed.
            if (DateTimeOffset.UtcNow - lastRefresh >= _options.LockRefreshInterval)
            {
                var refreshed = await _lock.TryRefresh(
                    LockKey, _options.InstanceId, _options.LockTtl);

                if (!refreshed)
                {
                    _logger.LogWarning("Lock refresh failed — lost leadership.");
                    return;
                }

                lastRefresh = DateTimeOffset.UtcNow;
                _logger.LogDebug("Lock refreshed.");
            }

            await SafeDelay(_options.ScanInterval, stoppingToken);
        }
    }

    private static async Task SafeDelay(TimeSpan delay, CancellationToken ct)
    {
        try { await Task.Delay(delay, ct); }
        catch (OperationCanceledException) { /* graceful shutdown */ }
    }
}