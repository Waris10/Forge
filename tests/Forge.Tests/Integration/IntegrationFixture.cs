using Dapper;
using Forge.Storage.Postgres;
using Npgsql;
using StackExchange.Redis;
using Testcontainers.PostgreSql;
using Testcontainers.Redis;

namespace Forge.Tests.Integration;

/// <summary>
/// Shared fixture: one Redis + one Postgres container per test run.
/// Each test class calls ResetAsync() in its constructor or setup
/// to get a clean slate without paying container startup again.
///
/// xUnit creates one instance per [Collection], shares it across
/// all classes in that collection, and disposes at the end.
/// </summary>
public class IntegrationFixture : IAsyncLifetime
{
    private readonly PostgreSqlContainer _pg = new PostgreSqlBuilder("postgres:16-alpine")
    .WithDatabase("forge")
    .WithUsername("forge")
    .WithPassword("forge")
    .Build();

    private readonly RedisContainer _redis = new RedisBuilder("redis:7-alpine")
        .Build();

    public string PgConnectionString => _pg.GetConnectionString();
    public string RedisConnectionString => _redis.GetConnectionString();

    public IConnectionMultiplexer Redis { get; private set; } = null!;

    public async Task InitializeAsync()
    {
        await _pg.StartAsync();
        await _redis.StartAsync();

        var redisConfig = ConfigurationOptions.Parse(RedisConnectionString);
        redisConfig.AllowAdmin = true;
        Redis = await ConnectionMultiplexer.ConnectAsync(redisConfig);

        // Required: same Dapper snake_case mapping the production code uses.
        // Without this, job_type -> JobType, last_error -> LastError etc. all fail.
        DapperConfig.Configure();

        // Apply the schema migration.
        await using var conn = new NpgsqlConnection(PgConnectionString);
        await conn.OpenAsync();

        var migrationPath = FindMigrationFile();
        var sql = await File.ReadAllTextAsync(migrationPath);
        await conn.ExecuteAsync(sql);
    }

    /// <summary>
    /// Flush Redis + truncate Postgres between tests so each
    /// test starts from a clean state without restarting containers.
    /// </summary>
    public async Task ResetAsync()
    {
        // Redis: flush the entire database.
        var server = Redis.GetServer(Redis.GetEndPoints().First());
        await server.FlushDatabaseAsync();

        // Postgres: truncate the jobs table.
        await using var conn = new NpgsqlConnection(PgConnectionString);
        await conn.OpenAsync();
        await conn.ExecuteAsync("TRUNCATE TABLE jobs");
    }

    public async Task DisposeAsync()
    {
        Redis.Dispose();
        await _pg.DisposeAsync();
        await _redis.DisposeAsync();
    }

    /// <summary>
    /// Walk up from the test binary directory to find the migration file.
    /// The migration lives at src/Forge.Storage/Postgres/Migrations/001_init.sql
    /// relative to the repo root.
    /// </summary>
    private static string FindMigrationFile()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null)
        {
            var candidate = Path.Combine(dir.FullName, "src", "Forge.Storage",
                "Postgres", "Migrations", "001_init.sql");
            if (File.Exists(candidate)) return candidate;
            dir = dir.Parent;
        }
        throw new FileNotFoundException(
            "Could not find 001_init.sql. Run tests from the repo root.");
    }
}
