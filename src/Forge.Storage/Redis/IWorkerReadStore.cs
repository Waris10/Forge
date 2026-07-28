namespace Forge.Storage.Redis;

public interface IWorkerReadStore
{
    Task<IReadOnlyList<WorkerSnapshot>> ListActiveAsync(CancellationToken ct);
}

public sealed record WorkerSnapshot(
    string WorkerId,
    TimeSpan HeartbeatTtl,
    long ProcessingCount);