using System.Text.Json;
using Forge.Core;
using Forge.Storage.Postgres;
using Forge.Storage.Redis;

namespace Forge.Tests.Integration;

[Collection("Integration")]
public class RetryExhaustionTests : IAsyncLifetime
{
    private readonly IntegrationFixture _f;
    private readonly RedisJobQueue _queue;
    private readonly JobRepository _repo;

    public RetryExhaustionTests(IntegrationFixture fixture)
    {
        _f = fixture;
        _queue = new RedisJobQueue(_f.Redis);
        _repo = new JobRepository(_f.PgConnectionString);
    }

    public Task InitializeAsync() => _f.ResetAsync();
    public Task DisposeAsync() => Task.CompletedTask;

    [Fact]
    public async Task Job_Exhausts_Retries_And_Lands_In_DLQ()
    {
        // Arrange: create a job with maxAttempts=3.
        var job = Job.NewQueued("Flaky",
            JsonSerializer.SerializeToElement(new { }),
            maxAttempts: 3);
        await _repo.Insert(job, CancellationToken.None);
        await _queue.Enqueue("default", job.Id, CancellationToken.None);

        // Simulate 3 failed attempts — exactly the retry loop in ExecutorService.
        for (var attempt = 1; attempt <= 3; attempt++)
        {
            // Pull.
            var pulled = await _queue.BlockingPull(
                "default", "test-worker", TimeSpan.FromSeconds(2), CancellationToken.None);
            Assert.NotNull(pulled);

            // Mark running in Postgres.
            await _repo.MarkRunning(job.Id, CancellationToken.None);

            if (attempt < 3)
            {
                // Not exhausted yet — reschedule with backoff.
                var nextRun = DateTimeOffset.UtcNow.AddSeconds(-1); // due immediately for testing
                await _queue.RescheduleFromProcessing(
                    "test-worker", job.Id, nextRun, CancellationToken.None);
                await _repo.MarkRetrying(job.Id, nextRun, $"Error on attempt {attempt}",
                    CancellationToken.None);

                // Promote back to ready queue so next iteration can pull.
                await _queue.PromoteDueJobs(batch: 100, CancellationToken.None);
            }
            else
            {
                // Exhausted — move to DLQ.
                await _queue.MoveToDlq("test-worker", job.Id, CancellationToken.None);
                await _repo.MarkDead(job.Id, "Final failure", CancellationToken.None);
            }
        }

        // Assert: job is in the DLQ.
        var db = _f.Redis.GetDatabase();
        var dlqLen = await db.ListLengthAsync(RedisKeys.Dlq);
        Assert.Equal(1, dlqLen);

        // Postgres shows status=dead.
        var final = await _repo.Get(job.Id, CancellationToken.None);
        Assert.NotNull(final);
        Assert.Equal(JobStatus.Dead, final!.Status);
        Assert.Equal("Final failure", final.LastError);
        Assert.Equal(3, final.Attempts);
    }

    [Fact]
    public async Task Job_Succeeds_Before_Exhaustion()
    {
        // Arrange: maxAttempts=3, succeeds on attempt 2.
        var job = Job.NewQueued("Flaky",
            JsonSerializer.SerializeToElement(new { }),
            maxAttempts: 3);
        await _repo.Insert(job, CancellationToken.None);
        await _queue.Enqueue("default", job.Id, CancellationToken.None);

        // Attempt 1: fails.
        await _queue.BlockingPull(
            "default", "test-worker", TimeSpan.FromSeconds(2), CancellationToken.None);
        await _repo.MarkRunning(job.Id, CancellationToken.None);
        var nextRun = DateTimeOffset.UtcNow.AddSeconds(-1);
        await _queue.RescheduleFromProcessing(
            "test-worker", job.Id, nextRun, CancellationToken.None);
        await _repo.MarkRetrying(job.Id, nextRun, "Transient error", CancellationToken.None);
        await _queue.PromoteDueJobs(batch: 100, CancellationToken.None);

        // Attempt 2: succeeds.
        await _queue.BlockingPull(
            "default", "test-worker", TimeSpan.FromSeconds(2), CancellationToken.None);
        await _repo.MarkRunning(job.Id, CancellationToken.None);
        await _queue.Ack("test-worker", job.Id, CancellationToken.None);
        await _repo.MarkSucceeded(job.Id, durationMs: 42, CancellationToken.None);

        // Assert: succeeded, not dead.
        var final = await _repo.Get(job.Id, CancellationToken.None);
        Assert.NotNull(final);
        Assert.Equal(JobStatus.Succeeded, final!.Status);
        Assert.Equal(2, final.Attempts);
        Assert.Equal(42, final.DurationMs);

        // DLQ should be empty.
        var db = _f.Redis.GetDatabase();
        var dlqLen = await db.ListLengthAsync(RedisKeys.Dlq);
        Assert.Equal(0, dlqLen);
    }
}
