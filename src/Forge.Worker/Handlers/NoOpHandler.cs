using System.Text.Json;
using Microsoft.Extensions.Logging;

namespace Forge.Worker.Handlers;

/// <summary>
/// A handler that does nothing except log that it ran. Used to verify the
/// end-to-end pipeline (submit → enqueue → pull → execute → ack → mark
/// succeeded) without any real work happening. Also useful for benchmarking
/// the queue itself, since the handler's runtime is essentially zero.
///
/// Registered in DI under the job-type key "NoOp".
/// </summary>
public class NoOpHandler : IJobHandler
{
    private readonly ILogger<NoOpHandler> _logger;

    public NoOpHandler(ILogger<NoOpHandler> logger)
    {
        _logger = logger;
    }

    public Task Handle(JsonElement payload, CancellationToken ct)
    {
        _logger.LogInformation("NoOp did the thing. Payload kind: {Kind}", payload.ValueKind);
        return Task.CompletedTask;
    }
}