using StackExchange.Redis;

namespace Forge.Storage.Redis;

public sealed class RedisWorkerReadStore : IWorkerReadStore
{
    private readonly IConnectionMultiplexer _redis;

    public RedisWorkerReadStore(IConnectionMultiplexer redis) => _redis = redis;

    public async Task<IReadOnlyList<WorkerSnapshot>> ListActiveAsync(CancellationToken ct)
    {
        var db = _redis.GetDatabase();
        var server = _redis.GetServer(_redis.GetEndPoints().First());

        var workers = new List<WorkerSnapshot>();

        // SCAN forge:heartbeat:* — same SCAN-not-KEYS pattern as M5.
        var keys = server.KeysAsync(pattern: "forge:heartbeat:*", pageSize: 100);

        await foreach (var key in keys.WithCancellation(ct))
        {
            // forge:heartbeat:worker-host-1234  ->  worker-host-1234
            var workerId = ((string)key!)["forge:heartbeat:".Length..];

            // TTL remaining tells us how recently the worker wrote the heartbeat.
            // Writer uses 30s TTL with 10s refresh cadence (per M5 worker config),
            // so a healthy worker's TTL bounces between ~20s and 30s.
            var ttl = await db.KeyTimeToLiveAsync(key);
            if (ttl is null) continue;  // race: key expired between SCAN and TTL

            var processingCount = await db.ListLengthAsync(
                RedisKeys.Processing(workerId));

            workers.Add(new WorkerSnapshot(workerId, ttl.Value, processingCount));
        }

        return workers
            .OrderBy(w => w.WorkerId, StringComparer.Ordinal)
            .ToList();
    }
}