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

    /// <summary>
    /// Move a job from this worker's processing list onto the scheduled zset
    /// with a score equal to <paramref name="runAt"/>'s unix-ms. The scheduler
    /// will promote it back to the ready queue when its time comes.
    ///
    /// Used by the executor's retry path: handler threw, we computed a backoff
    /// delay, now we reschedule. The atomicity of "remove from processing
    /// AND add to scheduled" matters: a worker crash mid-call could otherwise
    /// duplicate the job (still in processing, also in scheduled) or lose it
    /// (gone from processing, never made it to scheduled).
    /// </summary>
    Task RescheduleFromProcessing(
        string workerId,
        Guid jobId,
        DateTimeOffset runAt,
        CancellationToken ct);

    /// <summary>
    /// Move a job from this worker's processing list onto the dead letter
    /// queue. Called when retries are exhausted. Same atomicity concern as
    /// <see cref="RescheduleFromProcessing"/>.
    /// </summary>
    Task MoveToDlq(string workerId, Guid jobId, CancellationToken ct);

    /// <summary>
    /// Promote any jobs on the scheduled zset whose score is &lt;= now back to
    /// their queue (LPUSH). Returns the number promoted. Called periodically
    /// by the scheduler.
    ///
    /// Implementation in M3: multi-command (ZRANGEBYSCORE / ZREM / LPUSH).
    /// Atomicity is loose — two scheduler instances could promote the same
    /// job twice. M4 fixes this by replacing the body with a Lua script that
    /// runs server-side as a single atomic operation, plus a singleton lock
    /// across scheduler instances.
    /// </summary>
    Task<int> PromoteDueJobs(int batch, CancellationToken ct);
}