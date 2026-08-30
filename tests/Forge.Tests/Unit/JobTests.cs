using System.Text.Json;
using Forge.Core;

namespace Forge.Tests.Unit;

public class JobTests
{
    [Fact]
    public void NewQueued_Sets_Correct_Defaults()
    {
        var payload = JsonSerializer.SerializeToElement(new { foo = "bar" });
        var job = Job.NewQueued("SendEmail", payload);

        Assert.NotEqual(Guid.Empty, job.Id);
        Assert.Equal("SendEmail", job.JobType);
        Assert.Equal("default", job.Queue);
        Assert.Equal(5, job.Priority);
        Assert.Equal(JobStatus.Queued, job.Status);
        Assert.Equal(0, job.Attempts);
        Assert.Equal(5, job.MaxAttempts);
        Assert.Null(job.LastError);
        Assert.Null(job.IdempotencyKey);
        Assert.Null(job.ScheduledFor);
        Assert.Null(job.StartedAt);
        Assert.Null(job.CompletedAt);
        Assert.Null(job.DurationMs);
    }

    [Fact]
    public void NewQueued_Respects_Custom_Values()
    {
        var payload = JsonSerializer.SerializeToElement(new { });
        var scheduled = DateTimeOffset.UtcNow.AddMinutes(10);

        var job = Job.NewQueued(
            jobType: "Resize",
            payload: payload,
            queue: "images",
            priority: 1,
            maxAttempts: 10,
            idempotencyKey: "abc-123",
            scheduledFor: scheduled);

        Assert.Equal("images", job.Queue);
        Assert.Equal(1, job.Priority);
        Assert.Equal(10, job.MaxAttempts);
        Assert.Equal("abc-123", job.IdempotencyKey);
        Assert.Equal(scheduled, job.ScheduledFor);
    }

    [Fact]
    public void NewQueued_Generates_Unique_Ids()
    {
        var payload = JsonSerializer.SerializeToElement(new { });
        var a = Job.NewQueued("A", payload);
        var b = Job.NewQueued("B", payload);

        Assert.NotEqual(a.Id, b.Id);
    }

    [Fact]
    public void NewQueued_Stamps_CreatedAt_Near_Now()
    {
        var before = DateTimeOffset.UtcNow;
        var payload = JsonSerializer.SerializeToElement(new { });
        var job = Job.NewQueued("X", payload);
        var after = DateTimeOffset.UtcNow;

        Assert.InRange(job.CreatedAt, before, after);
    }
}
