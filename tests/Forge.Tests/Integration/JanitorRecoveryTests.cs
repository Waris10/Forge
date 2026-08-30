using System.Text.Json;
using Forge.Core;
using Forge.Storage.Postgres;
using Forge.Storage.Redis;

namespace Forge.Tests.Integration;

[Collection("Integration")]
public class JanitorRecoveryTests : IAsyncLifetime
{
    private readonly IntegrationFixture _f;
    private readonly RedisJobQueue _queue;
    private readonly JobRepository _repo;

    public JanitorRecoveryTests(IntegrationFixture fixture)
    {
        _f = fixture;
        _queue = new RedisJobQueue(_f.Redis);
        _repo = new JobRepository(_f.PgConnectionString);
    }

    public Task InitializeAsync() => _f.ResetAsync();
    public Task DisposeAsync() => Task.CompletedTask;

    [Fact]
    public async Task CrashedWorker_JobRequeued_By_Janitor()
    {
        // Arrange: submit and pull a job, simulating a worker taking it.
        var job = Job.NewQueued("Slow", JsonSerializer.SerializeToElement(new { }));
        await _repo.Insert(job, CancellationToken.None);
        await _queue.Enqueue("default", job.Id, CancellationToken.None);

        await _queue.BlockingPull(
            "default", "dead-worker", TimeSpan.FromSeconds(2), CancellationToken.None);

        // The job is now in dead-worker's processing list.
        // Simulate crash: the worker wrote a heartbeat, but it expired.
        // We never write a heartbeat at all — so FindDeadWorkers should
        // find "dead-worker" because its processing list is non-empty
        // and its heartbeat key is missing.

        // Act: janitor finds dead workers.
        var dead = await _queue.FindDeadWorkers(CancellationToken.None);

        Assert.Single(dead);
        Assert.Equal("dead-worker", dead[0]);

        // Act: janitor recovers the dead worker's jobs.
        var (recovered, poisoned) = await _queue.RecoverDeadWorker(
            "dead-worker", maxRequeue: 3, CancellationToken.None);

        Assert.Equal(1, recovered);
        Assert.Equal(0, poisoned);

        // Assert: job is back on the ready queue.
        var pulled = await _queue.BlockingPull(
            "default", "healthy-worker", TimeSpan.FromSeconds(2), CancellationToken.None);

        Assert.NotNull(pulled);
        Assert.Equal(job.Id, pulled!.Value);
    }

    [Fact]
    public async Task PoisonPill_Goes_To_DLQ_After_MaxRequeue()
    {
        var job = Job.NewQueued("Toxic", JsonSerializer.SerializeToElement(new { }));
        await _repo.Insert(job, CancellationToken.None);
        await _queue.Enqueue("default", job.Id, CancellationToken.None);

        // Simulate 3 crash-and-recover cycles with maxRequeue=3.
        for (var i = 0; i < 3; i++)
        {
            var workerId = $"crash-worker-{i}";
            await _queue.BlockingPull(
                "default", workerId, TimeSpan.FromSeconds(2), CancellationToken.None);
            // No heartbeat, no ack — worker "crashed."
            await _queue.RecoverDeadWorker(workerId, maxRequeue: 3, CancellationToken.None);
        }

        // After 3 requeues, the next recovery should poison it.
        // Actually, the requeue_count is now 3, which equals maxRequeue.
        // Let's pull + recover one more time to trigger the poison path.
        // Wait — RecoverDeadWorker increments requeue_count and checks
        // if count >= threshold. After 3 increments, count=3 >= 3 → poisoned.
        // So the third recovery should have poisoned it.

        // Assert: job should be in the DLQ.
        var db = _f.Redis.GetDatabase();
        var dlqLen = await db.ListLengthAsync(RedisKeys.Dlq);
        Assert.Equal(1, dlqLen);

        // And the ready queue should be empty.
        var readyLen = await db.ListLengthAsync(RedisKeys.Queue("default"));
        Assert.Equal(0, readyLen);
    }

    [Fact]
    public async Task Healthy_Worker_Not_Flagged_As_Dead()
    {
        var job = Job.NewQueued("NoOp", JsonSerializer.SerializeToElement(new { }));
        await _repo.Insert(job, CancellationToken.None);
        await _queue.Enqueue("default", job.Id, CancellationToken.None);

        // Pull the job.
        await _queue.BlockingPull(
            "default", "alive-worker", TimeSpan.FromSeconds(2), CancellationToken.None);

        // Write a heartbeat — this worker is alive.
        await _queue.Heartbeat("alive-worker", TimeSpan.FromSeconds(30), CancellationToken.None);

        // FindDeadWorkers should return empty — the worker has a heartbeat.
        var dead = await _queue.FindDeadWorkers(CancellationToken.None);
        Assert.Empty(dead);
    }
}
