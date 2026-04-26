using System.Text.Json;

namespace Forge.Worker.Handlers;

/// <summary>
/// The contract every job handler implements. The executor resolves a handler
/// by job-type string, then calls <see cref="Handle"/> with the job's payload.
///
/// For Milestone 2 we use the non-generic form — the handler receives the raw
/// <see cref="JsonElement"/> and is responsible for deserializing it. A typed
/// IJobHandler&lt;TPayload&gt; that does deserialization for the handler is a
/// nice ergonomic upgrade for later (Milestone 3+), but it's a wrapper over
/// this same interface, not a replacement.
/// </summary>
public interface IJobHandler
{
    /// <summary>
    /// Process the job. Return normally on success; throw on failure.
    /// The token is the per-job cancellation token (linked to the worker's
    /// shutdown token + a per-job timeout once we add timeouts in M3).
    /// </summary>
    Task Handle(JsonElement payload, CancellationToken ct);
}