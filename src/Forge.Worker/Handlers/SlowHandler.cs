using System.Text.Json;
using Microsoft.Extensions.Logging;

namespace Forge.Worker.Handlers;

/// <summary>
/// A handler that sleeps for <c>durationSeconds</c> from the payload before
/// returning. Lets us submit a job, watch it start running, then kill the
/// worker mid-execution — so the janitor has something to recover.
///
/// Defaults to 30 seconds if no duration is given. Plenty of time to
/// taskkill the worker.
///
/// Registered under the job-type key "Slow".
/// </summary>
public class SlowHandler : IJobHandler
{
    private readonly ILogger<SlowHandler> _logger;

    public SlowHandler(ILogger<SlowHandler> logger)
    {
        _logger = logger;
    }

    public async Task Handle(JsonElement payload, CancellationToken ct)
    {
        var seconds = 30;

        if (payload.ValueKind == JsonValueKind.Object &&
            payload.TryGetProperty("durationSeconds", out var d) &&
            d.ValueKind == JsonValueKind.Number)
        {
            seconds = d.GetInt32();
        }

        _logger.LogInformation("Slow handler sleeping for {Seconds}s", seconds);
        await Task.Delay(TimeSpan.FromSeconds(seconds), ct);
        _logger.LogInformation("Slow handler done.");
    }
}