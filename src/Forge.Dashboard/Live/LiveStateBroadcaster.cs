using Forge.Core;
using Forge.Storage.Redis;
using Microsoft.Extensions.Options;
using StackExchange.Redis;

namespace Forge.Dashboard.Live;

/// <summary>
/// Snapshot of live counters pushed to all connected circuits.
/// Cheap to construct, cheap to compare, immutable.
/// </summary>
public sealed record LiveCounters(
    long ReadyDepth,
    long ScheduledDepth,
    long DlqDepth,
    int WorkersAlive,
    DateTime CapturedAt);

/// <summary>
/// Single source of truth for live state in the dashboard.
///
/// Why a singleton + event instead of per-circuit polling:
/// every Blazor circuit polling Redis independently fans out
/// linearly with viewer count. One poller + one event means
/// 50 viewers cost the same as 1.
///
/// Components subscribe in OnInitializedAsync and unsubscribe
/// in Dispose. The broadcaster outlives all circuits.
/// </summary>
public sealed class LiveStateBroadcaster : BackgroundService
{
    private readonly IConnectionMultiplexer _redis;
    private readonly DashboardOptions _options;
    private readonly ILogger<LiveStateBroadcaster> _logger;

    private LiveCounters _latest = new(0, 0, 0, 0, DateTime.UtcNow);

    /// <summary>Latest snapshot. Components read this on first render
    /// to avoid a flash of zeros before the first tick.</summary>
    public LiveCounters Latest => _latest;

    /// <summary>Fired on every tick. Subscribers run on the broadcaster's
    /// thread — keep handlers fast and marshal to the UI thread via
    /// InvokeAsync if you need to mutate component state.</summary>
    public event Action<LiveCounters>? OnCountersChanged;

    public LiveStateBroadcaster(
        IConnectionMultiplexer redis,
        IOptions<DashboardOptions> options,
        ILogger<LiveStateBroadcaster> logger)
    {
        _redis = redis;
        _options = options.Value;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var db = _redis.GetDatabase();
        var server = _redis.GetServers().First(); // for SCAN of heartbeats

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                // Cheap O(1) reads. LLEN/ZCARD never scan.
                var ready = await db.ListLengthAsync(RedisKeys.Queue(_options.DefaultQueue));
                var scheduled = await db.SortedSetLengthAsync(RedisKeys.Scheduled);
                var dlq = await db.ListLengthAsync(RedisKeys.Dlq);

                // Workers alive: SCAN heartbeats, count keys.
                // SCAN over KEYS — same reason M5 made the switch.
                var workers = 0;
                await foreach (var _ in server.KeysAsync(pattern: "forge:heartbeat:*", pageSize: 100))
                    workers++;

                var snapshot = new LiveCounters(ready, scheduled, dlq, workers, DateTime.UtcNow);
                _latest = snapshot;
                OnCountersChanged?.Invoke(snapshot);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Live counter tick failed; will retry");
            }

            await Task.Delay(_options.LiveCounterInterval, stoppingToken);
        }
    }
}