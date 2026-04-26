namespace Forge.Janitor;

/// <summary>
/// Configuration for the janitor. Same singleton-via-lock model as the
/// scheduler — multiple instances run, one holds the lock, the others
/// take over on death/expiry.
/// </summary>
public class JanitorOptions
{
    public string InstanceId { get; set; } =
        $"janitor-{Environment.MachineName.ToLowerInvariant()}-{Environment.ProcessId}";

    public TimeSpan LockTtl { get; set; } = TimeSpan.FromSeconds(30);
    public TimeSpan LockRefreshInterval { get; set; } = TimeSpan.FromSeconds(10);
    public TimeSpan LockAcquireRetryInterval { get; set; } = TimeSpan.FromSeconds(5);

    /// <summary>
    /// How often the janitor scans for dead workers. Slower than the
    /// scheduler's tick because death-detection latency of a few seconds
    /// is fine — the heartbeat TTL is 30s, so we won't notice deaths any
    /// faster than that anyway. 5s is a reasonable cadence.
    /// </summary>
    public TimeSpan ScanInterval { get; set; } = TimeSpan.FromSeconds(5);

    /// <summary>
    /// A job recovered this many times is treated as a poison pill and
    /// sent to the DLQ instead of being requeued. 3 is the spec default —
    /// a job that's killed three different workers is almost certainly a
    /// real bug, not bad luck.
    /// </summary>
    public int MaxRequeueCount { get; set; } = 3;
}