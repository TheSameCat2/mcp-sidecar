using System.Data.Common;
using System.Security.Cryptography;
using System.Text;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Npgsql;

namespace McpSidecar.Services.Database;

public sealed class DbConnectionFactory : IDbConnectionFactory
{
    private const string DefaultSqlitePathTemplate = "~/.local/share/mcp-sidecar/{workspace_hash}.db";

    private readonly ILogger<DbConnectionFactory> _logger;
    private readonly string _postgresConnectionString;
    private readonly string _sqliteDatabasePath;
    private readonly SemaphoreSlim _sqliteInitLock = new(1, 1);
    private bool _sqliteInitialized;

    public DbConnectionFactory(
        IConfiguration configuration,
        ILogger<DbConnectionFactory> logger)
    {
        _logger = logger;

        var options = new DatabaseOptions();
        configuration.GetSection("Database").Bind(options);

        var envPostgresConnection =
            configuration["MCP_POSTGRES_CONNECTION"]
            ?? configuration["POSTGRES_CONNECTION_STRING"];
        var configuredProvider = options.Provider;

        Provider = ResolveProvider(configuredProvider, envPostgresConnection);
        _postgresConnectionString =
            options.Postgres.ConnectionString
            ?? configuration.GetConnectionString("Postgres")
            ?? envPostgresConnection
            ?? string.Empty;

        var workspaceRoot = ResolveWorkspaceRoot(configuration);
        _sqliteDatabasePath = ResolveSqlitePath(
            options.Sqlite.DatabasePath,
            workspaceRoot);

        if (Provider == "postgres" && string.IsNullOrWhiteSpace(_postgresConnectionString))
        {
            _logger.LogWarning("Database provider is set to postgres but no connection string was found.");
        }

        _logger.LogInformation("Database provider selected: {Provider}", Provider);
    }

    public string Provider { get; }

    public bool IsConfigured =>
        string.Equals(Provider, "sqlite", StringComparison.OrdinalIgnoreCase)
        || !string.IsNullOrWhiteSpace(_postgresConnectionString);

    public async Task<DbConnection> OpenConnectionAsync(CancellationToken cancellationToken = default)
    {
        if (string.Equals(Provider, "postgres", StringComparison.OrdinalIgnoreCase))
        {
            if (string.IsNullOrWhiteSpace(_postgresConnectionString))
            {
                throw new InvalidOperationException("Postgres provider selected, but no connection string is configured.");
            }

            var postgresConnection = new NpgsqlConnection(_postgresConnectionString);
            await postgresConnection.OpenAsync(cancellationToken);
            return postgresConnection;
        }

        await EnsureSqliteInitializedAsync(cancellationToken);

        var sqliteConnection = new SqliteConnection(CreateSqliteConnectionString());
        await sqliteConnection.OpenAsync(cancellationToken);

        await using (var pragma = sqliteConnection.CreateCommand())
        {
            pragma.CommandText = "PRAGMA foreign_keys = ON;";
            await pragma.ExecuteNonQueryAsync(cancellationToken);
        }

        return sqliteConnection;
    }

    private async Task EnsureSqliteInitializedAsync(CancellationToken cancellationToken)
    {
        if (_sqliteInitialized)
        {
            return;
        }

        await _sqliteInitLock.WaitAsync(cancellationToken);
        try
        {
            if (_sqliteInitialized)
            {
                return;
            }

            var databaseDirectory = Path.GetDirectoryName(_sqliteDatabasePath);
            if (!string.IsNullOrWhiteSpace(databaseDirectory))
            {
                Directory.CreateDirectory(databaseDirectory);
            }

            await using var connection = new SqliteConnection(CreateSqliteConnectionString());
            await connection.OpenAsync(cancellationToken);

            await using (var pragma = connection.CreateCommand())
            {
                pragma.CommandText = "PRAGMA foreign_keys = ON;";
                await pragma.ExecuteNonQueryAsync(cancellationToken);
            }

            var isInitialized = await IsSqliteSchemaInitializedAsync(connection, cancellationToken);
            if (!isInitialized)
            {
                var schemaPath = ResolveSqliteSchemaPath();
                var schemaSql = await File.ReadAllTextAsync(schemaPath, cancellationToken);
                await using var command = connection.CreateCommand();
                command.CommandText = schemaSql;
                await command.ExecuteNonQueryAsync(cancellationToken);
                _logger.LogInformation("Initialized SQLite schema: {DatabasePath}", _sqliteDatabasePath);
            }

            _sqliteInitialized = true;
        }
        finally
        {
            _sqliteInitLock.Release();
        }
    }

    private static async Task<bool> IsSqliteSchemaInitializedAsync(SqliteConnection connection, CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT 1
            FROM sqlite_master
            WHERE type = 'table'
              AND name = 'snapshot'
            LIMIT 1;
            """;

        var result = await command.ExecuteScalarAsync(cancellationToken);
        return result != null && result != DBNull.Value;
    }

    private string CreateSqliteConnectionString()
    {
        var builder = new SqliteConnectionStringBuilder
        {
            DataSource = _sqliteDatabasePath,
            Mode = SqliteOpenMode.ReadWriteCreate,
            Cache = SqliteCacheMode.Shared
        };
        return builder.ToString();
    }

    private static string ResolveProvider(string? configuredProvider, string? envPostgresConnection)
    {
        if (!string.IsNullOrWhiteSpace(envPostgresConnection))
        {
            // Preserve backward compatibility: legacy env var always selects Postgres.
            return "postgres";
        }

        if (!string.IsNullOrWhiteSpace(configuredProvider))
        {
            return configuredProvider.Trim().ToLowerInvariant() switch
            {
                "sqlite" => "sqlite",
                "postgres" or "postgresql" => "postgres",
                _ => "sqlite"
            };
        }

        return "sqlite";
    }

    private static string ResolveWorkspaceRoot(IConfiguration configuration)
    {
        var configuredRoot =
            configuration["MCP_WORKSPACE_ROOT"]
            ?? Environment.GetEnvironmentVariable("MCP_WORKSPACE_ROOT")
            ?? Directory.GetCurrentDirectory();

        return NormalizePath(configuredRoot);
    }

    private static string ResolveSqlitePath(string? pathTemplate, string workspaceRoot)
    {
        var template = string.IsNullOrWhiteSpace(pathTemplate) ? DefaultSqlitePathTemplate : pathTemplate;
        var workspaceHash = ComputeWorkspaceHash(workspaceRoot);
        var resolved = template!.Replace("{workspace_hash}", workspaceHash, StringComparison.OrdinalIgnoreCase);
        resolved = ExpandHomePath(resolved);

        if (!Path.IsPathRooted(resolved))
        {
            resolved = Path.Combine(workspaceRoot, resolved);
        }

        return NormalizePath(resolved);
    }

    private static string ResolveSqliteSchemaPath()
    {
        var candidates = new[]
        {
            Path.Combine(AppContext.BaseDirectory, "Schema", "schema-sqlite.sql"),
            Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "Schema", "schema-sqlite.sql")),
            Path.Combine(Directory.GetCurrentDirectory(), "src", "McpSidecar", "Schema", "schema-sqlite.sql"),
            Path.Combine(Directory.GetCurrentDirectory(), "Schema", "schema-sqlite.sql")
        };

        foreach (var candidate in candidates)
        {
            if (File.Exists(candidate))
            {
                return candidate;
            }
        }

        throw new FileNotFoundException("Unable to locate SQLite schema file (schema-sqlite.sql).");
    }

    private static string ExpandHomePath(string path)
    {
        if (!path.StartsWith("~", StringComparison.Ordinal))
        {
            return path;
        }

        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        var remainder = path.TrimStart('~').TrimStart(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        return Path.Combine(home, remainder);
    }

    private static string ComputeWorkspaceHash(string workspaceRoot)
    {
        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes(workspaceRoot));
        var hex = Convert.ToHexString(bytes).ToLowerInvariant();
        return hex[..16];
    }

    private static string NormalizePath(string path)
    {
        return Path.GetFullPath(path).Replace('\\', '/');
    }
}
