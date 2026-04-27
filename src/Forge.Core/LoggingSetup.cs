using Serilog;
using Serilog.Events;
using Serilog.Formatting.Compact;

namespace Forge.Core;

/// <summary>
/// Centralized Serilog configuration. Each app calls
/// <see cref="ConfigureSerilog"/> from its host builder so all four
/// processes log identically — same fields, same format, same level rules.
///
/// Output shape (per FORGE.md §11): compact JSON to stdout. One line per
/// event. Fields like @t (timestamp), @m (message), @l (level), @x
/// (exception). Custom properties (JobId, JobType, etc.) appear at the
/// top level.
///
/// In dev you can pipe to `jq -C '.'` for color + indent. In production a
/// log aggregator parses each line directly.
/// </summary>
public static class LoggingSetup
{
    /// <summary>
    /// Build a Serilog logger configuration for an app named
    /// <paramref name="appName"/>. The app name appears as the
    /// "Application" property on every log line — useful when tailing
    /// logs from multiple processes simultaneously.
    /// </summary>
    public static LoggerConfiguration Build(string appName)
    {
        return new LoggerConfiguration()
            .MinimumLevel.Information()
            .MinimumLevel.Override("Microsoft", LogEventLevel.Warning)
            .MinimumLevel.Override("Microsoft.Hosting.Lifetime", LogEventLevel.Information)
            .Enrich.FromLogContext()
            .Enrich.WithProperty("Application", appName)
            .WriteTo.Console(new CompactJsonFormatter());
    }
}