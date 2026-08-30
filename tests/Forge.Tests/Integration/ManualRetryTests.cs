using System.Text.Json;
using Forge.Core;
using Forge.Storage.Postgres;
using Forge.Storage.Redis;

namespace Forge.Tests.Integration;

[Collection("Integration")]
public class ManualRetryTests : IAsyncLifetime
{
    private readonly IntegrationFixture _f;
    private readonly RedisJobQueue _queue;
    private readonly JobRepository _repo;

    public ManualRetryTests(IntegrationFixture fixture)
    {
        _f = fixture;
        _queue = new RedisJobQueue(_f.Redis);
        _repo = new JobRepository(_f.PgConnectionString);
    }

    public Task InitializeAsync() => _f.ResetAsync();
    public Task DisposeAsync() => Task.CompletedTask;

    [Fact]
    public async Task Retry_Failed_Job_Requeues_And_Resets()
    {
        // Arrange: create a job, run it, fail it terminally.
        var job = Job.NewQueued("Flaky",
            JsonSerializer.SerializeToElement(new { }),
            maxAttempts: 1);
        await _repo.Insert(job, CancellationToken.None);
        await _queue.Enqueue("default", job.Id, CancellationToken.None);

        await _queue.BlockingPull(
            "default", "w1", TimeSpan.FromSeconds(2), CancellationToken.None);
        await _repo.MarkRunning(job.Id, CancellationToken.None);
        await _queue.MoveToDlq("w1", job.Id, CancellationToken.None);
        await _repo.MarkDead(job.Id, "boom", CancellationToken.None);

        // Verify it's dead.
        var dead = await _repo.Get(job.Id, CancellationToken.None);
        Assert.Equal(JobStatus.Dead, dead!.Status);
        Assert.Equal(1, dead.Attempts);
        Assert.Equal(1, dead.MaxAttempts);

        // Act: manual retry (same as POST /jobs/{id}/retry).
        await _repo.MarkForRetry(job.Id, CancellationToken.None);
        await _queue.Enqueue("default", job.Id, CancellationToken.None);

        // Assert: Postgres row is reset.
        var retried = await _repo.Get(job.Id, CancellationToken.None);
        Assert.Equal(JobStatus.Queued, retried!.Status);
        Assert.Null(retried.LastError);
        Assert.Null(retried.CompletedAt);
        Assert.Equal(2, retried.MaxAttempts); // attempts(1) + 1

        // Assert: job is pullable from Redis.
        var pulled = await _queue.BlockingPull(
            "default", "w2", TimeSpan.FromSeconds(2), CancellationToken.None);
        Assert.Equal(job.Id, pulled);
    }

    [Fact]
    public async Task Retry_NonTerminal_Job_Does_Nothing()
    {
        // Arrange: create a running job.
        var job = Job.NewQueued("NoOp", JsonSerializer.SerializeToElement(new { }));
        await _repo.Insert(job, CancellationToken.None);
        await _repo.MarkRunning(job.Id, CancellationToken.None);

        // Act: attempt retry — SQL has WHERE status IN ('failed','dead').
        await _repo.MarkForRetry(job.Id, CancellationToken.None);

        // Assert: status unchanged (still running).
        var after = await _repo.Get(job.Id, CancellationToken.None);
        Assert.Equal(JobStatus.Running, after!.Status);
    }
}
