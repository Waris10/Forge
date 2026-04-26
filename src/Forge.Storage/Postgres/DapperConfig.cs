using Dapper;

namespace Forge.Storage.Postgres;

/// <summary>
/// One-time Dapper setup. Call <see cref="Configure"/> once at process start,
/// typically from Program.cs right after building the host.
/// </summary>
public static class DapperConfig
{
    private static bool _configured;

    public static void Configure()
    {
        if (_configured) return;

        // Map snake_case column names (job_type) to PascalCase property names (JobType).
        // Without this, Dapper would set JobType only if a column was literally named
        // "JobType" — which never happens in a sensibly-named Postgres schema.
        DefaultTypeMap.MatchNamesWithUnderscores = true;

        _configured = true;
    }
}