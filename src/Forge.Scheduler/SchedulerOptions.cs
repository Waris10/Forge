namespace Forge.Scheduler;

/// <summary>
/// Configuration for a single scheduler instance. Bound from the "Scheduler"
/// section of appsettings.json.
///
/// Multiple instances can run simultaneously; the distributed lock ensures
/// only one promotes at a time. The other(s) idle, periodically retrying
/// the lock — when the active instance dies, one of them takes over within
/// roughly <see cref="LockTtl"/>.
/// </summary>
public class SchedulerOptions
{
    /// <summary>
    /// Stable identifier for this scheduler instance. Stored as the value
    /// of the lock key so we can verify ownership before releasing.
    ///
    /// Defaults to "scheduler-{machineName}-{processId}". In Docker you'd
    /// override this with the container hostname, e.g. via env var.
    /// </summary>
    public string InstanceId { get; set; } =
        $"scheduler-{Environment.MachineName.ToLowerInvariant()}-{Environment.ProcessId}";

    /// <summary>
    /// How long the lock lives before auto-expiring. The active instance
    /// refreshes the TTL every <see cref="LockRefreshInterval"/>, so the
    /// lock stays held as long as the process is healthy. If the process
    /// dies, the TTL expires and another instance can take over within
    /// roughly this duration.
    ///
    /// Spec recommends 30s. Long enough that brief GC pauses or network
    /// blips don't lose the lock; short enough that takeover is timely.
    /// </summary>
    public TimeSpan LockTtl { get; set; } = TimeSpan.FromSeconds(30);

    /// <summary>
    /// How often the holder refreshes the lock TTL. Should be well under
    /// <see cref="LockTtl"/> so a missed tick or two doesn't lose the lock.
    /// </summary>
    public TimeSpan LockRefreshInterval { get; set; } = TimeSpan.FromSeconds(10);

    /// <summary>
    /// How often non-holding instances retry acquiring the lock. Doesn't
    /// need to be very fast — when the holder dies, takeover happens via
    /// TTL expiry anyway, not via aggressive polling.
    /// </summary>
    public TimeSpan LockAcquireRetryInterval { get; set; } = TimeSpan.FromSeconds(5);

    /// <summary>
    /// Tick interval for the active promoter — how often it calls
    /// PromoteDueJobs while it holds the lock.
    /// </summary>
    public TimeSpan TickInterval { get; set; } = TimeSpan.FromSeconds(1);

    /// <summary>
    /// Max jobs promoted per tick. Bounds the work each tick does so a
    /// huge backlog can't make a single ZRANGEBYSCORE slow.
    /// </summary>
    public int BatchSize { get; set; } = 100;
}