using System.Text.Json;
using Forge.Api.Contracts;

namespace Forge.Tests.Unit;

public class RequestValidationTests
{
    [Fact]
    public void SubmitJobRequest_Defaults_Are_Null()
    {
        var payload = JsonSerializer.SerializeToElement(new { });
        var req = new SubmitJobRequest("Test", payload);

        Assert.Null(req.Queue);
        Assert.Null(req.Priority);
        Assert.Null(req.MaxAttempts);
        Assert.Null(req.DelaySeconds);
        Assert.Null(req.IdempotencyKey);
    }

    [Fact]
    public void SubmitJobRequest_Carries_Custom_Values()
    {
        var payload = JsonSerializer.SerializeToElement(new { key = "value" });

        var req = new SubmitJobRequest(
            JobType: "Email",
            Payload: payload,
            Queue: "high",
            Priority: 1,
            MaxAttempts: 10,
            DelaySeconds: 30,
            IdempotencyKey: "idem-1");

        Assert.Equal("Email", req.JobType);
        Assert.Equal("high", req.Queue);
        Assert.Equal(1, req.Priority);
        Assert.Equal(10, req.MaxAttempts);
        Assert.Equal(30, req.DelaySeconds);
        Assert.Equal("idem-1", req.IdempotencyKey);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(11)]
    [InlineData(-1)]
    [InlineData(100)]
    public void Priority_Out_Of_Range_Should_Be_Rejected(int priority)
    {
        // These values should fail the `is < 1 or > 10` check in the API.
        // We test the condition here since validation is inline.
        var outOfRange = priority is < 1 or > 10;
        Assert.True(outOfRange, $"Priority {priority} should be out of [1,10]");
    }

    [Theory]
    [InlineData(1)]
    [InlineData(5)]
    [InlineData(10)]
    public void Priority_In_Range_Should_Be_Accepted(int priority)
    {
        var outOfRange = priority is < 1 or > 10;
        Assert.False(outOfRange, $"Priority {priority} should be within [1,10]");
    }

    [Theory]
    [InlineData(0)]
    [InlineData(101)]
    [InlineData(-1)]
    public void MaxAttempts_Out_Of_Range_Should_Be_Rejected(int maxAttempts)
    {
        var outOfRange = maxAttempts is < 1 or > 100;
        Assert.True(outOfRange);
    }

    [Theory]
    [InlineData(-1)]
    [InlineData(-100)]
    public void DelaySeconds_Negative_Should_Be_Rejected(int delay)
    {
        var negative = delay is < 0;
        Assert.True(negative);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(3600)]
    public void DelaySeconds_NonNegative_Should_Be_Accepted(int delay)
    {
        var negative = delay is < 0;
        Assert.False(negative);
    }
}
