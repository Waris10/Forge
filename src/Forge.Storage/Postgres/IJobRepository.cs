using Forge.Core;

namespace Forge.Storage.Postgres;

/// <summary>
/// Read/write operations against the jobs table. One instance per scope
/// (registered as scoped in DI) so connections can be pooled sensibly.
/// </summary>
public interface IJobRepository
{
    /// <summary>
    /// Insert a new job row. Throws if the id or idempotency_key already exists.
    /// </summary>
    Task Insert(Job job, CancellationToken ct);

    /// <summary>
    /// Fetch a job by id, or null if it doesn't exist.
    /// </summary>
    Task<Job?> Get(Guid id, CancellationToken ct);

    /// <summary>
    /// Look up an existing job by its idempotency key. Returns null if not found.
    /// Used by POST /jobs to make repeat submissions idempotent.
    /// </summary>
    Task<Job?> FindByIdempotencyKey(string key, CancellationToken ct);

    /// <summary>
    /// Mark a job as running and stamp <c>started_at</c>. Increments <c>attempts</c>.
    /// Called by the executor immediately before invoking the handler.
    /// </summary>
    Task MarkRunning(Guid id, CancellationToken ct);

    /// <summary>
    /// Mark a job as succeeded and stamp <c>completed_at</c> + <c>duration_ms</c>.
    /// Called after the handler returns without throwing.
    /// </summary>
    Task MarkSucceeded(Guid id, int durationMs, CancellationToken ct);

    /// <summary>
    /// Mark a job as failed (terminal in M2 — replaced by retry logic in M3).
    /// Stores <c>last_error</c> and stamps <c>completed_at</c>.
    /// </summary>
    Task MarkFailed(Guid id, string error, int durationMs, CancellationToken ct);

    /// <summary>
    /// Mark a job for retry: status returns to 'queued', <c>last_error</c> is
    /// recorded, <c>scheduled_for</c> stamps when the job will be eligible to
    /// run again. Note: <c>attempts</c> was already incremented by
    /// <see cref="MarkRunning"/> before the handler ran, so we don't bump it
    /// again here.
    /// </summary>
    Task MarkRetrying(Guid id, DateTimeOffset nextRunAt, string error, CancellationToken ct);

    /// <summary>
    /// Mark a job dead — terminal, no more retries. Called when attempts have
    /// been exhausted. Stamps <c>completed_at</c> and <c>last_error</c>.
    /// </summary>
    Task MarkDead(Guid id, string error, CancellationToken ct);
}