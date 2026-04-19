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
}