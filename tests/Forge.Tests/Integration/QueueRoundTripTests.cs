using System.Text.Json;
using Forge.Core;
using Forge.Storage.Postgres;
using Forge.Storage.Redis;

namespace Forge.Tests.Integration;

[Collection("Integration")]
public class QueueRoundTripTests : IAsyncLifetime
{
    private readonly IntegrationFixture _f;
    private readonly RedisJobQueue _queue;
    private readonly JobRepository _repo;

    public QueueRoundTripTests(IntegrationFixture fixture)
    {
        _f = fixture;
        _queue = new RedisJobQueue(_f.Redis);
        _repo = new JobRepository(_f.PgConnectionString);
    }

    public Task InitializeAsync() => _f.ResetAsync();
    public Task DisposeAsync() => Task.CompletedTask;

    [Fact]
    public async Task Enqueue_Pull_Ack_CompleteRoundTrip()
    {
        // Arrange: insert a job into Postgres, enqueue into Redis.
        var job = Job.NewQueued("NoOp", JsonSerializer.SerializeToElement(new { }));
        await _repo.Insert(job, CancellationToken.None);
        await _queue.Enqueue("default", job.Id, CancellationToken.None);

        // Act: pull the job.
        var pulled = await _queue.BlockingPull(
            "default", "test-worker", TimeSpan.FromSeconds(2), CancellationToken.None);

        // Assert: we got the right job.
        Assert.NotNull(pulled);
        Assert.Equal(job.Id, pulled!.Value);

        // Act: ack it.
        await _queue.Ack("test-worker", job.Id, CancellationToken.None);

        // Assert: processing list is empty.
        var db = _f.Redis.GetDatabase();
        var processingLen = await db.ListLengthAsync(RedisKeys.Processing("test-worker"));
        Assert.Equal(0, processingLen);
    }

    [Fact]
    public async Task Pull_Returns_Null_When_Queue_Empty()
    {
        var pulled = await _queue.BlockingPull(
            "default", "test-worker", TimeSpan.FromSeconds(1), CancellationToken.None);

        Assert.Null(pulled);
    }

    [Fact]
    public async Task Enqueue_Creates_PerJob_Hash()
    {
        var job = Job.NewQueued("Email", JsonSerializer.SerializeToElement(new { }));
        await _repo.Insert(job, CancellationToken.None);
        await _queue.Enqueue("default", job.Id, CancellationToken.None);

        var db = _f.Redis.GetDatabase();
        var queue = await db.HashGetAsync(RedisKeys.Job(job.Id), "queue");

        Assert.Equal("default", queue.ToString());
    }

    [Fact]
    public async Task Pull_Moves_Job_To_Processing_List()
    {
        var job = Job.NewQueued("NoOp", JsonSerializer.SerializeToElement(new { }));
        await _repo.Insert(job, CancellationToken.None);
        await _queue.Enqueue("default", job.Id, CancellationToken.None);

        await _queue.BlockingPull(
            "default", "test-worker", TimeSpan.FromSeconds(2), CancellationToken.None);

        var db = _f.Redis.GetDatabase();
        var processingLen = await db.ListLengthAsync(RedisKeys.Processing("test-worker"));
        Assert.Equal(1, processingLen);

        var readyLen = await db.ListLengthAsync(RedisKeys.Queue("default"));
        Assert.Equal(0, readyLen);
    }
}
