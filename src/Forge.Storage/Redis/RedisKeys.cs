namespace Forge.Storage.Redis;

/// <summary>
/// The canonical Redis key namespace for Forge. Every Redis key anywhere in the
/// codebase must come from here. No ad-hoc string concatenation at call sites.
///
/// The "forge:" prefix scopes all our keys so you can share a Redis instance
/// with other apps if you ever need to. `KEYS forge:*` gives you everything
/// we own, nothing we don't.
///
/// Key conventions:
///   forge:queue:{queueName}         LIST  — ready jobs, FIFO (LPUSH left, pop right)
///   forge:processing:{workerId}     LIST  — in-flight jobs on one worker
///   forge:scheduled                 ZSET  — delayed jobs, score = unix ms
///   forge:dlq                       LIST  — dead letter queue
///   forge:job:{jobId}               HASH  — optional payload cache (not used yet)
///   forge:heartbeat:{workerId}      STRING with TTL — liveness signal
///   forge:lock:scheduler            STRING with TTL — scheduler singleton lock
///   forge:idempotency:{key}         STRING with TTL — idempotency cache (optional)
/// </summary>
public static class RedisKeys
{
    private const string Prefix = "forge";

    /// <summary>The ready queue for a given queue name. LPUSH new, BLMOVE off the right.</summary>
    public static string Queue(string queueName) => $"{Prefix}:queue:{queueName}";

    /// <summary>The per-worker processing list. Jobs live here while executing.</summary>
    public static string Processing(string workerId) => $"{Prefix}:processing:{workerId}";

    /// <summary>
    /// Per-job metadata hash, e.g. forge:job:{guid}. Stores at minimum the
    /// "queue" field, so the scheduler's Lua script can route a promoted job
    /// back to its original queue. Other fields can be added later (priority,
    /// idempotency_key, etc.) without breaking anyone — Redis hashes are
    /// schemaless.
    /// </summary>
    public static string Job(Guid jobId) => $"forge:job:{jobId}";

    /// <summary>The sorted set of scheduled/delayed jobs, scored by unix ms.</summary>
    public static string Scheduled => $"{Prefix}:scheduled";

    /// <summary>Dead letter queue for jobs that exhausted retries.</summary>
    public static string Dlq => $"{Prefix}:dlq";

    /// <summary>Per-worker heartbeat key with TTL. Janitor uses this to detect dead workers.</summary>
    public static string Heartbeat(string workerId) => $"{Prefix}:heartbeat:{workerId}";

    /// <summary>The global scheduler lock — only one scheduler instance holds this.</summary>
    public static string SchedulerLock => $"{Prefix}:lock:scheduler";
}