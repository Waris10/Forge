namespace Forge.Dashboard;

public sealed class DashboardOptions
{
    /// <summary>How often the broadcaster polls Redis for live counters.</summary>
    public TimeSpan LiveCounterInterval { get; set; } = TimeSpan.FromSeconds(1);

    /// <summary>How often the broadcaster polls Postgres for recent job changes.</summary>
    public TimeSpan JobListPollInterval { get; set; } = TimeSpan.FromSeconds(2);

    /// <summary>Base URL of Jaeger for trace links on the job detail page.</summary>
    public string JaegerBaseUrl { get; set; } = "http://localhost:16686";

    /// <summary>Base URL of Grafana for the dashboard link.</summary>
    public string GrafanaBaseUrl { get; set; } = "http://localhost:3000";

    /// <summary>Default queue name to display on the ops page.</summary>
    public string DefaultQueue { get; set; } = "default";
}