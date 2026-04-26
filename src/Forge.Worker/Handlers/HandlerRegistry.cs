namespace Forge.Worker.Handlers;

/// <summary>
/// Maps job-type strings to handler instances. The executor calls
/// <see cref="Resolve"/> with a job's <c>JobType</c> and gets back the
/// handler that should run it.
///
/// Registered as a singleton in DI. The handlers themselves are resolved
/// from the DI container at registration time so they can have their own
/// dependencies (loggers, HTTP clients, etc.) injected.
/// </summary>
public class HandlerRegistry
{
    private readonly IReadOnlyDictionary<string, IJobHandler> _handlers;

    public HandlerRegistry(IDictionary<string, IJobHandler> handlers)
    {
        _handlers = new Dictionary<string, IJobHandler>(handlers, StringComparer.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Returns the handler for a given job type, or null if no handler is
    /// registered. The executor treats null as "unknown job type" — a fatal
    /// error for that job, since there's no point retrying something nothing
    /// can handle.
    /// </summary>
    public IJobHandler? Resolve(string jobType)
        => _handlers.TryGetValue(jobType, out var handler) ? handler : null;
}