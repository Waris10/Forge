using StackExchange.Redis;

namespace Forge.Storage.Redis;

/// <summary>
/// Redis implementation of <see cref="IJobQueue"/>. Uses LPUSH + BLMOVE for
/// reliable FIFO queueing.
///
/// Registered as a singleton in DI — the underlying <see cref="IConnectionMultiplexer"/>
/// is the thing you want to share across the entire process. Creating one per
/// request would thrash connections and lose the multiplexer's pipelining benefits.
/// </summary>
public class RedisJobQueue : IJobQueue
{
    private readonly IConnectionMultiplexer _redis;

    public RedisJobQueue(IConnectionMultiplexer redis)
    {
        _redis = redis;
    }

    public async Task Enqueue(string queueName, Guid jobId, CancellationToken ct)
    {
        var db = _redis.GetDatabase();
        // LPUSH = push onto the left (head) of the list. Workers pull from the
        // right (tail) via BLMOVE, giving us FIFO. New jobs wait behind existing
        // jobs, oldest job runs next.
        await db.ListLeftPushAsync(RedisKeys.Queue(queueName), jobId.ToString());
    }

    public async Task<Guid?> BlockingPull(
        string queueName,
        string workerId,
        TimeSpan timeout,
        CancellationToken ct)
    {
        var db = _redis.GetDatabase();

        // BLMOVE <source> <destination> RIGHT LEFT <timeout>
        //
        // Atomically: pop from the right of the source list (oldest job), push
        // to the left of the destination list, block up to `timeout` seconds
        // if source is empty. Returns nil on timeout.
        //
        // This is the heart of reliable queueing. If the worker crashes
        // between this call returning and Ack() being called, the job id sits
        // in the processing list and the janitor can recover it.
        var result = await db.ListMoveAsync(
            sourceKey: RedisKeys.Queue(queueName),
            destinationKey: RedisKeys.Processing(workerId),
            sourceSide: ListSide.Right,
            destinationSide: ListSide.Left);

        // StackExchange.Redis's ListMoveAsync is non-blocking. For truly blocking
        // behavior we'd use ExecuteAsync with a raw BLMOVE command. For Milestone 2
        // we simulate blocking by polling with a short sleep when empty — this
        // keeps the code simple and we'll upgrade to true BLMOVE later.
        if (result.IsNull)
        {
            try
            {
                await Task.Delay(TimeSpan.FromMilliseconds(200), ct);
            }
            catch (OperationCanceledException)
            {
                return null;
            }
            return null;
        }

        return Guid.TryParse(result.ToString(), out var id) ? id : null;
    }

    public async Task Ack(string workerId, Guid jobId, CancellationToken ct)
    {
        var db = _redis.GetDatabase();

        // LREM count=1: remove up to 1 occurrence of the job id from the
        // processing list. After this, the job is fully "done" from Redis's
        // point of view. Postgres still holds its history row.
        await db.ListRemoveAsync(RedisKeys.Processing(workerId), jobId.ToString(), count: 1);
    }

    public async Task RescheduleFromProcessing(
    string workerId,
    Guid jobId,
    DateTimeOffset runAt,
    CancellationToken ct)
    {
        var db = _redis.GetDatabase();
        var idStr = jobId.ToString();
        var score = runAt.ToUnixTimeMilliseconds();

        // Two operations, batched into a single RTT via CreateBatch. Not atomic
        // (a crash between the two leaves duplicates in scheduled OR an orphan
        // in processing), but the worst case is a job runs once extra — at-least-
        // once is our delivery contract anyway. M4's Lua script makes it atomic.
        var batch = db.CreateBatch();
        var removeTask = batch.ListRemoveAsync(
            RedisKeys.Processing(workerId), idStr, count: 1);
        var addTask = batch.SortedSetAddAsync(
            RedisKeys.Scheduled, idStr, score);
        batch.Execute();

        await Task.WhenAll(removeTask, addTask);
    }

    public async Task MoveToDlq(string workerId, Guid jobId, CancellationToken ct)
    {
        var db = _redis.GetDatabase();
        var idStr = jobId.ToString();

        var batch = db.CreateBatch();
        var removeTask = batch.ListRemoveAsync(
            RedisKeys.Processing(workerId), idStr, count: 1);
        var pushTask = batch.ListLeftPushAsync(RedisKeys.Dlq, idStr);
        batch.Execute();

        await Task.WhenAll(removeTask, pushTask);
    }

    public async Task<int> PromoteDueJobs(int batch, CancellationToken ct)
    {
        var db = _redis.GetDatabase();

        // Use Redis's clock, not the worker's. Cheap (single RTT) and avoids
        // surprises when worker clocks drift.
        var serverTime = await db.ExecuteAsync("TIME");
        // TIME returns [seconds, microseconds]; we want milliseconds.
        var parts = (RedisResult[])serverTime!;
        var nowMs = (long.Parse((string)parts[0]!) * 1000)
                  + (long.Parse((string)parts[1]!) / 1000);

        // Find up to `batch` job ids whose scheduled time has arrived.
        var due = await db.SortedSetRangeByScoreAsync(
            key: RedisKeys.Scheduled,
            start: 0,
            stop: nowMs,
            order: Order.Ascending,
            take: batch);

        if (due.Length == 0) return 0;

        // For each due id: remove from the zset, push onto the ready queue.
        // We don't currently track per-job queue routing in Redis (the spec's
        // Lua script stores it in a per-job hash; we'll add that in M4 when
        // we move to Lua). For now, every promoted job goes to "default".
        //
        // This is a known M3 simplification. The promoted jobs lose any custom
        // queue assignment until M4. In practice we only have "default" anyway.
        var queueKey = RedisKeys.Queue("default");

        var b = db.CreateBatch();
        var tasks = new List<Task>(due.Length * 2);
        foreach (var id in due)
        {
            tasks.Add(b.SortedSetRemoveAsync(RedisKeys.Scheduled, id));
            tasks.Add(b.ListLeftPushAsync(queueKey, id));
        }
        b.Execute();

        await Task.WhenAll(tasks);

        return due.Length;
    }
}