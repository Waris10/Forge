using Forge.Storage.Redis;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Forge.Worker;

/// <summary>
/// Periodically promotes due jobs from the scheduled zset back to the ready
/// queue. Runs once per second.
///
/// This is an M3 placeholder. M4 introduces a standalone <c>Forge.Scheduler</c>
/// process that does the same thing under a singleton lock with a Lua script
/// for atomicity. Until then, every worker runs its own copy of this loop —
/// which is fine for one worker, lossy in a fleet (multiple workers will race
/// to promote the same due jobs and produce duplicates). At-least-once
/// delivery tolerates that; M4 makes it once.
/// </summary>
public class SchedulerTickService : BackgroundService
{
    private static readonly TimeSpan TickInterval = TimeSpan.FromSeconds(1);
    private const int BatchSize = 100;

    private readonly IJobQueue _queue;
    private readonly ILogger<SchedulerTickService> _logger;

    public SchedulerTickService(IJobQueue queue, ILogger<SchedulerTickService> logger)
    {
        _queue = queue;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.LogInformation(
            "Scheduler tick started. Interval={Interval} BatchSize={BatchSize}",
            TickInterval, BatchSize);

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                var promoted = await _queue.PromoteDueJobs(BatchSize, stoppingToken);
                if (promoted > 0)
                {
                    _logger.LogInformation("Promoted {Count} due jobs from scheduled zset", promoted);
                }
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Scheduler tick error, backing off");
            }

            try { await Task.Delay(TickInterval, stoppingToken); }
            catch (OperationCanceledException) { break; }
        }

        _logger.LogInformation("Scheduler tick stopped.");
    }
}