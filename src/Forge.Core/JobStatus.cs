namespace Forge.Core;

/// <summary>
/// The lifecycle state of a job. Persisted to Postgres as lowercase strings
/// to match the CHECK constraint on the jobs.status column.
/// </summary>
public enum JobStatus
{
    /// <summary>In Redis ready queue OR waiting in the delayed zset. Not yet picked up.</summary>
    Queued,

    /// <summary>A worker has pulled it and is executing the handler right now.</summary>
    Running,

    /// <summary>Handler returned without throwing. Terminal state.</summary>
    Succeeded,

    /// <summary>Handler threw, but attempts remain — job is being retried.</summary>
    Failed,

    /// <summary>Handler exhausted max_attempts. Terminal state. Moved to DLQ.</summary>
    Dead
}