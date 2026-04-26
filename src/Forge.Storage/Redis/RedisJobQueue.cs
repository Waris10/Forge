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

    /// <summary>
    /// Atomically promotes due jobs from the scheduled zset back to their
    /// ready queues. Single Redis operation — no client can interleave.
    ///
    /// KEYS[1] = forge:scheduled (the zset)
    /// ARGV[1] = "now" in unix ms (passed in, not redis.call('TIME'),
    ///           because TIME is not deterministic and would break replication)
    /// ARGV[2] = batch size
    /// ARGV[3] = key prefix for per-job hashes  ("forge:job:")
    /// ARGV[4] = key prefix for queues          ("forge:queue:")
    ///
    /// Returns the number of jobs promoted.
    ///
    /// For each due job:
    ///   1. Remove from the zset (no longer "scheduled").
    ///   2. Look up its queue name from forge:job:{id}, default to "default".
    ///   3. LPUSH onto forge:queue:{queue}.
    ///
    /// All inside one server-side script -> atomic.
    /// </summary>
    private const string PromoteDueJobsLua = """
    local ids = redis.call('ZRANGEBYSCORE', KEYS[1], 0, ARGV[1], 'LIMIT', 0, ARGV[2])
    for i, id in ipairs(ids) do
      redis.call('ZREM', KEYS[1], id)
      local q = redis.call('HGET', ARGV[3] .. id, 'queue')
      if not q or q == false then
        q = 'default'
      end
      redis.call('LPUSH', ARGV[4] .. q, id)
    end
    return #ids
    """;

    private readonly IConnectionMultiplexer _redis;

    public RedisJobQueue(IConnectionMultiplexer redis)
    {
        _redis = redis;
    }

    public async Task Enqueue(string queueName, Guid jobId, CancellationToken ct)
    {
        var db = _redis.GetDatabase();
        var idStr = jobId.ToString();

        // Two writes: per-job hash (so the scheduler can route on promote)
        // and the LPUSH onto the ready queue. Pipelined into one RTT.
        var batch = db.CreateBatch();
        var hashTask = batch.HashSetAsync(
            RedisKeys.Job(jobId),
            new[] { new HashEntry("queue", queueName) });
        var pushTask = batch.ListLeftPushAsync(RedisKeys.Queue(queueName), idStr);
        batch.Execute();

        await Task.WhenAll(hashTask, pushTask);
    }


    public async Task Schedule(
    string queueName,
    Guid jobId,
    DateTimeOffset runAt,
    CancellationToken ct)
    {
        var db = _redis.GetDatabase();
        var idStr = jobId.ToString();
        var score = runAt.ToUnixTimeMilliseconds();

        var batch = db.CreateBatch();
        var hashTask = batch.HashSetAsync(
            RedisKeys.Job(jobId),
            new[] { new HashEntry("queue", queueName) });
        var addTask = batch.SortedSetAddAsync(RedisKeys.Scheduled, idStr, score);
        batch.Execute();

        await Task.WhenAll(hashTask, addTask);
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

        // Use Redis's clock as the source of truth, pass it in as ARGV.
        var serverTime = await db.ExecuteAsync("TIME");
        var parts = (RedisResult[])serverTime!;
        var nowMs = (long.Parse((string)parts[0]!) * 1000)
                  + (long.Parse((string)parts[1]!) / 1000);

        var result = await db.ScriptEvaluateAsync(
            PromoteDueJobsLua,
            keys: new RedisKey[] { RedisKeys.Scheduled },
            values: new RedisValue[]
            {
            nowMs,
            batch,
            "forge:job:",
            "forge:queue:"
            });

        // The script returns an integer count.
        return (int)(long)result;
    }
}