using Forge.Core;
using Forge.Storage;
using Forge.Storage.Postgres;
using Forge.Storage.Redis;
using Microsoft.Extensions.Options;
using StackExchange.Redis;

namespace Forge.Dashboard.Live;

public sealed record OverviewSnapshot(
    LiveCounters Counters,
    IReadOnlyList<ThroughputBucket> Throughput,
    IReadOnlyList<Job> RecentFailures,
    DateTime CapturedAt);

/// <summary>
/// Combines Redis live counters, Postgres throughput buckets, and
/// recent failures into a single snapshot pushed to the overview
/// page. One poller, one event, one subscription per circuit.
///
/// Polls at the LiveCounterInterval (1s) for the Redis gauges.
/// The Postgres queries (throughput + failures) run on every
/// other tick to avoid hammering the DB — the chart updates
/// every 2s, which is fast enough for a 5-minute window.
/// </summary>
public sealed class OverviewBroadcaster : BackgroundService
{
    private readonly IConnectionMultiplexer _redis;
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly DashboardOptions _options;
    private readonly ILogger<OverviewBroadcaster> _logger;

    private OverviewSnapshot _latest;
    private int _tickCount;

    public OverviewSnapshot Latest => _latest;
    public event Action<OverviewSnapshot>? OnOverviewChanged;

    public OverviewBroadcaster(
        IConnectionMultiplexer redis,
        IServiceScopeFactory scopeFactory,
        IOptions<DashboardOptions> options,
        ILogger<OverviewBroadcaster> logger)
    {
        _redis = redis;
        _scopeFactory = scopeFactory;
        _options = options.Value;
        _logger = logger;

        _latest = new OverviewSnapshot(
            new LiveCounters(0, 0, 0, 0, DateTime.UtcNow),
            Array.Empty<ThroughputBucket>(),
            Array.Empty<Job>(),
            DateTime.UtcNow);
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var db = _redis.GetDatabase();
        var server = _redis.GetServers().First();

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                // Redis counters — every tick (1s)
                var ready = await db.ListLengthAsync(RedisKeys.Queue(_options.DefaultQueue));
                var scheduled = await db.SortedSetLengthAsync(RedisKeys.Scheduled);
                var dlq = await db.ListLengthAsync(RedisKeys.Dlq);

                var workers = 0;
                await foreach (var _ in server.KeysAsync(pattern: "forge:heartbeat:*", pageSize: 100))
                    workers++;

                var counters = new LiveCounters(ready, scheduled, dlq, workers, DateTime.UtcNow);

                // Postgres queries — every other tick (~2s)
                var throughput = _latest.Throughput;
                var failures = _latest.RecentFailures;

                _tickCount++;
                if (_tickCount % 2 == 0)
                {
                    using var scope = _scopeFactory.CreateScope();
                    var repo = scope.ServiceProvider.GetRequiredService<IJobRepository>();

                    throughput = await repo.GetThroughputAsync(
                        window: TimeSpan.FromMinutes(5),
                        bucketSeconds: 5,
                        ct: stoppingToken);

                    failures = await repo.ListRecentFailuresAsync(
                        limit: 10,
                        ct: stoppingToken);
                }

                var snapshot = new OverviewSnapshot(counters, throughput, failures, DateTime.UtcNow);
                _latest = snapshot;
                OnOverviewChanged?.Invoke(snapshot);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Overview poll tick failed; will retry");
            }

            await Task.Delay(_options.LiveCounterInterval, stoppingToken);
        }
    }
}