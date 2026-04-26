using System.Text.Json;

namespace Forge.Worker.Handlers;

/// <summary>
/// A handler that succeeds with probability <c>successRate</c>, throws otherwise.
/// Used to exercise the retry path. Reads <c>successRate</c> from the payload
/// (a number 0..1) if present; defaults to 0.3 (70% failure rate, plenty of
/// retries to watch in the logs).
///
/// Registered under the job-type key "Flaky".
/// </summary>
public class FlakyHandler : IJobHandler
{
    private readonly ILogger<FlakyHandler> _logger;

    public FlakyHandler(ILogger<FlakyHandler> logger)
    {
        _logger = logger;
    }

    public Task Handle(JsonElement payload, CancellationToken ct)
    {
        var successRate = 0.3;

        if (payload.ValueKind == JsonValueKind.Object &&
            payload.TryGetProperty("successRate", out var sr) &&
            sr.ValueKind == JsonValueKind.Number)
        {
            successRate = sr.GetDouble();
        }

        var roll = Random.Shared.NextDouble();
        if (roll < successRate)
        {
            _logger.LogInformation(
                "Flaky succeeded (roll {Roll:F2} < {SuccessRate:F2})",
                roll, successRate);
            return Task.CompletedTask;
        }

        _logger.LogWarning(
            "Flaky failing (roll {Roll:F2} >= {SuccessRate:F2})",
            roll, successRate);
        throw new InvalidOperationException(
            $"Flaky simulated failure (roll {roll:F2})");
    }
}