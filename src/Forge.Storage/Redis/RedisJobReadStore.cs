using StackExchange.Redis;

namespace Forge.Storage.Redis;

public sealed class RedisJobReadStore : IJobReadStore
{
    private readonly IConnectionMultiplexer _redis;

    public RedisJobReadStore(IConnectionMultiplexer redis) => _redis = redis;

    public async Task<JobLiveSnapshot?> GetJobLiveAsync(Guid jobId, CancellationToken ct)
    {
        var db = _redis.GetDatabase();
        var entries = await db.HashGetAllAsync(RedisKeys.Job(jobId));
        if (entries.Length == 0) return null;

        string? queue = null, traceparent = null;
        var requeues = 0;

        foreach (var e in entries)
        {
            switch ((string)e.Name!)
            {
                case "queue": queue = e.Value; break;
                case "traceparent": traceparent = e.Value; break;
                case "requeue_count": int.TryParse(s: e.Value, out requeues); break;
            }
        }

        return new JobLiveSnapshot(queue, traceparent, requeues);
    }
}