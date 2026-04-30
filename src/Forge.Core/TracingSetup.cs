using System.Diagnostics;

namespace Forge.Core;

/// <summary>
/// Centralized OpenTelemetry tracing configuration. Each app that wants
/// to emit traces (API, Worker) calls into here from its Program.cs.
///
/// Why a static class plus an ActivitySource constant: .NET's tracing API
/// (System.Diagnostics.ActivitySource) requires a *named* source that the
/// SDK subscribes to. Code that emits spans calls Source.StartActivity(...).
/// Defining the source name in one place means every project agrees on
/// the name "Forge" — the SDK's subscription, the application code's
/// emission, and the trace data all match.
/// </summary>
public static class TracingSetup
{
    /// <summary>
    /// The single ActivitySource for all Forge spans. Consumers do:
    /// <code>using var act = TracingSetup.Source.StartActivity("name");</code>
    /// </summary>
    public static readonly ActivitySource Source = new("Forge");

    /// <summary>
    /// The service name reported in trace attributes. Set per-process
    /// (e.g. "Forge.Api", "Forge.Worker") so Jaeger groups spans by service.
    /// </summary>
    public const string SourceName = "Forge";
} 