using Forge.Core;
using Forge.Storage;
using Forge.Storage.Postgres;
using Microsoft.Extensions.Options;

namespace Forge.Dashboard.Live;

public sealed record DlqSnapshot(
    IReadOnlyList<Job> Jobs,
    DateTime CapturedAt);

/// <summary>
/// DLQ-specific broadcaster. Polls Postgres for the recent N jobs with
/// status='dead'. Separate from JobListBroadcaster because:
///   - The DLQ page is its own route with its own poll cadence.
///   - The query is fixed-filter (dead only), simpler than the firehose.
///   - Operators may keep the DLQ page open continuously; we don't want
///     the firehose driving it when they're focused on the DLQ subset.
/// </summary>
public sealed class DlqBroadcaster : BackgroundService
{
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly DashboardOptions _options;
    private readonly ILogger<DlqBroadcaster> _logger;

    private DlqSnapshot _latest = new(Array.Empty<Job>(), DateTime.UtcNow);

    public DlqSnapshot Latest => _latest;

    public event Action<DlqSnapshot>? OnDlqChanged;

    public DlqBroadcaster(
        IServiceScopeFactory scopeFactory,
        IOptions<DashboardOptions> options,
        ILogger<DlqBroadcaster> logger)
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
                using var scope = _scopeFactory.CreateScope();
                var repo = scope.ServiceProvider.GetRequiredService<IJobRepository>();

                var jobs = await repo.ListRecentAsync(
                    status: JobStatus.Dead,
                    limit: _options.JobListFirehoseSize,
                    ct: stoppingToken);

                var snapshot = new DlqSnapshot(jobs, DateTime.UtcNow);
                _latest = snapshot;
                OnDlqChanged?.Invoke(snapshot);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "DLQ poll tick failed; will retry");
            }

            await Task.Delay(_options.JobListPollInterval, stoppingToken);
        }
    }
}