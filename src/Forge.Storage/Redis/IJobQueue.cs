namespace Forge.Storage.Redis;

/// <summary>
/// Abstraction over the Redis-backed job queue. Milestone 2 exposes just the
/// minimum needed for end-to-end submission and execution:
///   - Enqueue: producer side, used by the API after inserting into Postgres.
///   - BlockingPull: consumer side, used by worker pullers. Atomically moves
///     a job from the ready queue to this worker's processing list (reliable
///     queueing via BLMOVE).
///   - Ack: consumer side, called after a successful handler run. Removes the
///     job from the processing list.
///
/// Later milestones add: Schedule, RescheduleFromProcessing, MoveToDlq,
/// PromoteDueJobs, Heartbeat. We'll grow this interface deliberately, not
/// stub it all out now.
/// </summary>
public interface IJobQueue
{
    /// <summary>
    /// Push a job id onto the ready queue. The producer calls this immediately
    /// after inserting the Postgres row. Idempotent at the Redis layer: pushing
    /// the same id twice means the job runs twice (at-least-once semantics).
    /// </summary>
    Task Enqueue(string queueName, Guid jobId, CancellationToken ct);

    /// <summary>
    /// Block-and-move: wait up to <paramref name="timeout"/> for a job in
    /// <paramref name="queueName"/>, then atomically move it to this worker's
    /// processing list. Returns null on timeout. Returns the job id on success.
    ///
    /// This is the reliable-queue pattern. The atomic move guarantees that a
    /// worker crash between receiving a job and acking it leaves the job
    /// visible in the processing list for the janitor to recover.
    /// </summary>
    Task<Guid?> BlockingPull(
        string queueName,
        string workerId,
        TimeSpan timeout,
        CancellationToken ct);

    /// <summary>
    /// Remove a job id from this worker's processing list. Called after
    /// successful handler execution (or after a retry has been rescheduled
    /// — in that case the job is already on the scheduled zset).
    /// </summary>
    Task Ack(string workerId, Guid jobId, CancellationToken ct);
}