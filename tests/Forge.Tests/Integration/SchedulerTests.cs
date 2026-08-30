using System.Text.Json;
using Forge.Core;
using Forge.Storage.Postgres;
using Forge.Storage.Redis;

namespace Forge.Tests.Integration;

[Collection("Integration")]
public class SchedulerTests : IAsyncLifetime
{
    private readonly IntegrationFixture _f;
    private readonly RedisJobQueue _queue;
    private readonly JobRepository _repo;

    public SchedulerTests(IntegrationFixture fixture)
    {
        _f = fixture;
        _queue = new RedisJobQueue(_f.Redis);
        _repo = new JobRepository(_f.PgConnectionString);
    }

    public Task InitializeAsync() => _f.ResetAsync();
    public Task DisposeAsync() => Task.CompletedTask;

    [Fact]
    public async Task ScheduledJob_Promoted_When_Due()
    {
        // Arrange: schedule a job for "now - 1 second" (already due).
        var job = Job.NewQueued("NoOp",
            JsonSerializer.SerializeToElement(new { }),
            scheduledFor: DateTimeOffset.UtcNow.AddSeconds(-1));
        await _repo.Insert(job, CancellationToken.None);
        await _queue.Schedule("default", job.Id, job.ScheduledFor!.Value, CancellationToken.None);

        // Ready queue should be empty before promotion.
        var db = _f.Redis.GetDatabase();
        var readyBefore = await db.ListLengthAsync(RedisKeys.Queue("default"));
        Assert.Equal(0, readyBefore);

        // Act: promote due jobs.
        var promoted = await _queue.PromoteDueJobs(batch: 100, CancellationToken.None);

        // Assert: one job promoted.
        Assert.Equal(1, promoted);

        // The job should now be on the ready queue.
        var readyAfter = await db.ListLengthAsync(RedisKeys.Queue("default"));
        Assert.Equal(1, readyAfter);

        // Scheduled set should be empty.
        var scheduledLen = await db.SortedSetLengthAsync(RedisKeys.Scheduled);
        Assert.Equal(0, scheduledLen);
    }

    [Fact]
    public async Task FutureJob_Not_Promoted()
    {
        // Arrange: schedule a job for 1 hour from now.
        var job = Job.NewQueued("NoOp",
            JsonSerializer.SerializeToElement(new { }),
            scheduledFor: DateTimeOffset.UtcNow.AddHours(1));
        await _repo.Insert(job, CancellationToken.None);
        await _queue.Schedule("default", job.Id, job.ScheduledFor!.Value, CancellationToken.None);

        // Act: promote due jobs.
        var promoted = await _queue.PromoteDueJobs(batch: 100, CancellationToken.None);

        // Assert: nothing promoted — job is not due yet.
        Assert.Equal(0, promoted);

        // Still in the scheduled set.
        var db = _f.Redis.GetDatabase();
        var scheduledLen = await db.SortedSetLengthAsync(RedisKeys.Scheduled);
        Assert.Equal(1, scheduledLen);
    }

    [Fact]
    public async Task Promotion_Routes_To_Correct_Queue()
    {
        // Arrange: schedule a job on the "email" queue.
        var job = Job.NewQueued("SendEmail",
            JsonSerializer.SerializeToElement(new { }),
            queue: "email",
            scheduledFor: DateTimeOffset.UtcNow.AddSeconds(-1));
        await _repo.Insert(job, CancellationToken.None);
        await _queue.Schedule("email", job.Id, job.ScheduledFor!.Value, CancellationToken.None);

        // Act: promote.
        await _queue.PromoteDueJobs(batch: 100, CancellationToken.None);

        // Assert: job landed on forge:queue:email, not forge:queue:default.
        var db = _f.Redis.GetDatabase();
        var emailLen = await db.ListLengthAsync(RedisKeys.Queue("email"));
        var defaultLen = await db.ListLengthAsync(RedisKeys.Queue("default"));
        Assert.Equal(1, emailLen);
        Assert.Equal(0, defaultLen);
    }

    [Fact]
    public async Task Singleton_Lock_Prevents_Double_Promotion()
    {
        // Arrange: schedule 5 due jobs.
        for (var i = 0; i < 5; i++)
        {
            var job = Job.NewQueued("NoOp",
                JsonSerializer.SerializeToElement(new { }),
                scheduledFor: DateTimeOffset.UtcNow.AddSeconds(-1));
            await _repo.Insert(job, CancellationToken.None);
            await _queue.Schedule("default", job.Id, job.ScheduledFor!.Value, CancellationToken.None);
        }

        // Act: two concurrent promote calls (simulating two scheduler instances).
        // The Lua script is atomic, so one will promote all 5 and the other
        // will find the set empty.
        var t1 = _queue.PromoteDueJobs(batch: 100, CancellationToken.None);
        var t2 = _queue.PromoteDueJobs(batch: 100, CancellationToken.None);
        var results = await Task.WhenAll(t1, t2);

        // Assert: total promoted across both calls is exactly 5.
        // One call got all 5, the other got 0 (or they split, but no
        // duplicates because ZREM in the Lua is atomic).
        Assert.Equal(5, results.Sum());

        // Ready queue has exactly 5 jobs.
        var db = _f.Redis.GetDatabase();
        var readyLen = await db.ListLengthAsync(RedisKeys.Queue("default"));
        Assert.Equal(5, readyLen);
    }
}
