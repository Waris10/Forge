using Forge.Storage.Redis;
using Microsoft.Extensions.Options;

namespace Forge.Dashboard.Live;

public sealed record WorkersSnapshot(
    IReadOnlyList<WorkerSnapshot> Workers,
    DateTime CapturedAt);

/// <summary>
/// Polls Redis for active workers (those with a live heartbeat key) plus
/// their current processing-list depth. Parallel to LiveStateBroadcaster
/// but scoped to the workers page — a 1s cadence is overkill there, but
/// matches the rest of the live UI so visual updates feel consistent.
/// </summary>
public sealed class WorkersBroadcaster : BackgroundService
{
    private readonly IWorkerReadStore _reader;
    private readonly DashboardOptions _options;
    private readonly ILogger<WorkersBroadcaster> _logger;

    private WorkersSnapshot _latest = new(
        Array.Empty<WorkerSnapshot>(), DateTime.UtcNow);

    public WorkersSnapshot Latest => _latest;

    public event Action<WorkersSnapshot>? OnWorkersChanged;

    public WorkersBroadcaster(
        IWorkerReadStore reader,
        IOptions<DashboardOptions> options,
        ILogger<WorkersBroadcaster> logger)
    {
        _reader = reader;
        _options = options.Value;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                var workers = await _reader.ListActiveAsync(stoppingToken);
                var snapshot = new WorkersSnapshot(workers, DateTime.UtcNow);
                _latest = snapshot;
                OnWorkersChanged?.Invoke(snapshot);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Workers poll tick failed; will retry");
            }

            await Task.Delay(_options.LiveCounterInterval, stoppingToken);
        }
    }
}