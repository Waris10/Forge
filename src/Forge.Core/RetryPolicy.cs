namespace Forge.Core;

/// <summary>
/// Computes how long to wait before the next attempt of a failed job.
///
/// Exponential backoff with full jitter: the base delay doubles each attempt
/// (2s, 4s, 8s, 16s, 32s...) and we add up to 1s of random noise on top.
///
/// Why jitter matters: without it, a failure pulse (downstream service goes
/// down, every job in flight throws) would produce a thundering herd of
/// retries firing at the exact same wall-clock instant. The jitter spreads
/// them across a one-second window — enough to break synchronization,
/// not enough to noticeably slow the retry cadence.
///
/// Pure function, no state, no DI. Static class is the right shape.
/// </summary>
public static class RetryPolicy
{
    /// <summary>
    /// Delay before attempt number <paramref name="attempt"/>. Attempt 1 is
    /// the *first retry* — i.e. the second time the handler will run total.
    /// So callers should pass <c>job.Attempts + 1</c> after a failure.
    /// </summary>
    /// <remarks>
    /// Capped at 5 minutes. Without a cap, attempt 10 would wait ~17 minutes,
    /// attempt 15 over 9 hours. For a job queue we'd rather DLQ a job than
    /// have it asymptotically retry forever.
    /// </remarks>
    public static TimeSpan Delay(int attempt)
    {
        if (attempt < 1) attempt = 1;

        // 2^attempt seconds. attempt=1 -> 2s, 2 -> 4s, 3 -> 8s, ...
        var baseSeconds = Math.Pow(2, attempt);

        // Cap at 5 minutes (300s) before adding jitter.
        var capped = Math.Min(baseSeconds, 300);

        var jitterMs = Random.Shared.Next(0, 1000);

        return TimeSpan.FromSeconds(capped) + TimeSpan.FromMilliseconds(jitterMs);
    }
}