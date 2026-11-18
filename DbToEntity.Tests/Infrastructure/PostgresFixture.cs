using System.Diagnostics;
using Npgsql;
using Testcontainers.PostgreSql;

namespace DbToEntity.Tests.Infrastructure;

/// <summary>A [Fact] that is skipped (not failed) when Docker isn't reachable, so the suite runs anywhere.</summary>
public sealed class DockerFactAttribute : FactAttribute
{
    public DockerFactAttribute()
    {
        if (!PostgresFixture.DockerAvailable) Skip = "Docker is not available (start Docker to run integration tests).";
    }
}

/// <summary>
/// One throwaway PostgreSQL 17 container per test run, loaded with <c>Integration/schema.sql</c>.
/// Tests must not change that schema; each test gets data isolation by using its own rows.
/// </summary>
public sealed class PostgresFixture : IAsyncLifetime
{
    private static readonly Lazy<bool> Docker = new(() =>
    {
        try
        {
            using var process = Process.Start(new ProcessStartInfo("docker", "info --format {{.ServerVersion}}")
            {
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
            })!;
            return process.WaitForExit(10_000) && process.ExitCode == 0;
        }
        catch
        {
            return false; // docker CLI not installed
        }
    });

    public static bool DockerAvailable => Docker.Value;

    private PostgreSqlContainer? _container;

    public string ConnectionString => _container?.GetConnectionString()
        ?? throw new InvalidOperationException("Postgres container not started (Docker unavailable).");

    public async Task InitializeAsync()
    {
        if (!DockerAvailable) return;

        _container = new PostgreSqlBuilder("postgres:17").Build();
        await _container.StartAsync();

        await using var conn = new NpgsqlConnection(ConnectionString);
        await conn.OpenAsync();
        var schema = await File.ReadAllTextAsync(Path.Combine(AppContext.BaseDirectory, "Integration", "schema.sql"));
        await using var cmd = new NpgsqlCommand(schema, conn);
        await cmd.ExecuteNonQueryAsync();
    }

    public async Task DisposeAsync()
    {
        if (_container != null) await _container.DisposeAsync();
    }
}

[CollectionDefinition(Name)]
public sealed class PostgresCollection : ICollectionFixture<PostgresFixture>
{
    public const string Name = "postgres";
}
