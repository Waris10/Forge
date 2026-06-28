using Forge.Core;
using Forge.Storage.Postgres;
using Microsoft.Extensions.Options;

namespace Forge.Dashboard.Live;

/// <summary>
/// Snapshot pushed to all unfiltered jobs-list subscribers.
/// Filtered subscribers don't use this — they run their own queries.
/// </summary>
public sealed record JobListSnapshot(
    IReadOnlyList<Job> Jobs,
    DateTime CapturedAt);

/// <summary>
/// Polls Postgres for the recent N jobs and broadcasts to circuits
/// that aren't applying a filter. Hybrid pattern: cheap shared
/// firehose, plus opt-in scoped queries from filtered components
/// (those bypass this broadcaster entirely).
///
/// Postgres polling is the M7 implementation. M8+ replaces this
/// with LISTEN/NOTIFY for zero-lag updates — same instinct as
/// M2's poll-based queue upgrading to BLMOVE in M3.
/// </summary>
public sealed class JobListBroadcaster : BackgroundService
{
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly DashboardOptions _options;
    private readonly ILogger<JobListBroadcaster> _logger;

    private JobListSnapshot _latest = new(Array.Empty<Job>(), DateTime.UtcNow);

    public JobListSnapshot Latest => _latest;

    public event Action<JobListSnapshot>? OnJobsChanged;

    public JobListBroadcaster(
        IServiceScopeFactory scopeFactory,
        IOptions<DashboardOptions> options,
        ILogger<JobListBroadcaster> logger)
    {
        _scopeFactory = scopeFactory;
        _options = options.Value;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                // IJobRepository is scoped, so we open a scope per tick.
                // Same pattern as M2's per-executor DI scope.
                using var scope = _scopeFactory.CreateScope();
                var repo = scope.ServiceProvider.GetRequiredService<IJobRepository>();

                var jobs = await repo.ListRecentAsync(
                    status: null,
                    limit: _options.JobListFirehoseSize,
                    ct: stoppingToken);

                var snapshot = new JobListSnapshot(jobs, DateTime.UtcNow);
                _latest = snapshot;
                OnJobsChanged?.Invoke(snapshot);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Job list poll tick failed; will retry");
            }

            await Task.Delay(_options.JobListPollInterval, stoppingToken);
        }
    }
}