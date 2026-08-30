using Forge.Core;

namespace Forge.Tests.Unit;

public class RetryPolicyTests
{
    [Theory]
    [InlineData(1, 2)]
    [InlineData(2, 4)]
    [InlineData(3, 8)]
    [InlineData(4, 16)]
    [InlineData(5, 32)]
    public void Delay_Returns_Exponential_Base(int attempt, int expectedBaseSeconds)
    {
        var delay = RetryPolicy.Delay(attempt);

        // Base is 2^attempt seconds. Jitter adds 0–1s.
        Assert.InRange(
            delay.TotalSeconds,
            expectedBaseSeconds,
            expectedBaseSeconds + 1.0);
    }

    [Fact]
    public void Delay_Caps_At_Five_Minutes()
    {
        // 2^20 = 1,048,576 seconds without cap — way past 5 min.
        var delay = RetryPolicy.Delay(20);

        // Capped at 300s + up to 1s jitter.
        Assert.InRange(delay.TotalSeconds, 300, 301);
    }

    [Fact]
    public void Delay_Has_Jitter()
    {
        // 50 samples at the same attempt; jitter should produce
        // more than one distinct value.
        var delays = Enumerable.Range(0, 50)
            .Select(_ => RetryPolicy.Delay(3).TotalMilliseconds)
            .Distinct()
            .ToList();

        Assert.True(delays.Count > 1, "Delay should include jitter");
    }

    [Fact]
    public void Delay_Clamps_Negative_Attempt_To_One()
    {
        var delay = RetryPolicy.Delay(-5);

        // Should behave like attempt=1: base=2s + 0-1s jitter.
        Assert.InRange(delay.TotalSeconds, 2, 3);
    }

    [Fact]
    public void Delay_Clamps_Zero_Attempt_To_One()
    {
        var delay = RetryPolicy.Delay(0);

        Assert.InRange(delay.TotalSeconds, 2, 3);
    }

    [Fact]
    public void Delay_Is_Monotonically_Increasing_Before_Cap()
    {
        // Average over multiple samples to smooth jitter.
        double Avg(int attempt) => Enumerable.Range(0, 20)
            .Average(_ => RetryPolicy.Delay(attempt).TotalSeconds);

        var d1 = Avg(1);
        var d2 = Avg(2);
        var d3 = Avg(3);
        var d4 = Avg(4);

        Assert.True(d1 < d2, "Attempt 2 should be longer than 1");
        Assert.True(d2 < d3, "Attempt 3 should be longer than 2");
        Assert.True(d3 < d4, "Attempt 4 should be longer than 3");
    }
}
