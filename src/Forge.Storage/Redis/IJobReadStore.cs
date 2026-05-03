namespace Forge.Storage.Redis;

public interface IJobReadStore
{
    Task<JobLiveSnapshot?> GetJobLiveAsync(Guid jobId, CancellationToken ct);
}

public sealed record JobLiveSnapshot(
    string? Queue,
    string? Traceparent,
    int RequeueCount);