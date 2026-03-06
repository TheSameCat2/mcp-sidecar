using System.Data.Common;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;

namespace McpSidecar.Services.Database;

public sealed class DbConnectionFactory : IDbConnectionFactory
{
    private const string DefaultSqlitePathTemplate = "~/.local/share/mcp-sidecar/{workspace_hash}.db";

    private readonly ILogger<DbConnectionFactory> _logger;
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

        var workspaceRoot = ResolveWorkspaceRoot(configuration);
        _sqliteDatabasePath = ResolveSqlitePath(
            options.Sqlite.DatabasePath,
            workspaceRoot);

        _logger.LogInformation("Using SQLite database: {DatabasePath}", _sqliteDatabasePath);
    }

    public string Provider => "sqlite";

    public bool IsConfigured => true;

    public async Task<DbConnection> OpenConnectionAsync(CancellationToken cancellationToken = default)
    {
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
                var schemaSql = await LoadEmbeddedSchemaAsync(cancellationToken);
                await using var command = connection.CreateCommand();
                command.CommandText = schemaSql;
                await command.ExecuteNonQueryAsync(cancellationToken);
                _logger.LogInformation("Initialized SQLite schema: {DatabasePath}", _sqliteDatabasePath);
            }
            else
            {
                var currentVersion = await GetSchemaVersionAsync(connection, cancellationToken);
                _logger.LogDebug("SQLite schema version: {Version}", currentVersion);
                
                // Apply migrations if needed
                await ApplyMigrationsAsync(connection, currentVersion, cancellationToken);
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

    private static async Task<int> GetSchemaVersionAsync(SqliteConnection connection, CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT version
            FROM schema_version
            ORDER BY version DESC
            LIMIT 1;
            """;

        var result = await command.ExecuteScalarAsync(cancellationToken);
        if (result == null || result == DBNull.Value)
        {
            return 0;
        }

        return Convert.ToInt32(result);
    }

    /// <summary>
    /// Applies schema migrations if the database is on an older version.
    /// </summary>
    private async Task ApplyMigrationsAsync(SqliteConnection connection, int currentVersion, CancellationToken cancellationToken)
    {
        if (currentVersion >= 2)
        {
            // Already up to date
            return;
        }

        // Migration 2: Add extraction_progress table
        if (currentVersion < 2)
        {
            _logger.LogInformation("Applying migration: version 2 (extraction_progress table)");
            
            await using var command = connection.CreateCommand();
            command.CommandText = """
                CREATE TABLE IF NOT EXISTS extraction_progress (
                    snapshot_id INTEGER PRIMARY KEY,
                    status TEXT NOT NULL CHECK (status IN ('running', 'completed', 'interrupted', 'failed')),
                    started_at TEXT NOT NULL DEFAULT CURRENT_TIMESTAMP,
                    completed_at TEXT,
                    files_total INTEGER NOT NULL DEFAULT 0,
                    files_processed INTEGER NOT NULL DEFAULT 0,
                    files_failed INTEGER NOT NULL DEFAULT 0,
                    last_file_processed TEXT,
                    error_message TEXT,
                    FOREIGN KEY (snapshot_id) REFERENCES snapshot(snapshot_id) ON DELETE CASCADE
                );
                
                CREATE INDEX IF NOT EXISTS ix_extraction_progress_status ON extraction_progress(status);
                
                INSERT INTO schema_version (version, description) VALUES (2, 'Added extraction_progress table for resume capability');
                """;
            
            await command.ExecuteNonQueryAsync(cancellationToken);
            _logger.LogInformation("Migration to version 2 complete");
        }
    }

    private static async Task<string> LoadEmbeddedSchemaAsync(CancellationToken cancellationToken)
    {
        var assembly = typeof(DbConnectionFactory).Assembly;

        // Try to find schema file in embedded resources
        // The resource name includes the project namespace
        var resourceName = assembly.GetManifestResourceNames()
            .FirstOrDefault(n => n.EndsWith("schema-sqlite.sql", StringComparison.OrdinalIgnoreCase));

        if (resourceName == null)
        {
            var available = string.Join(", ", assembly.GetManifestResourceNames());
            throw new InvalidOperationException($"Could not find embedded schema resource. Available: {available}");
        }

        using var stream = assembly.GetManifestResourceStream(resourceName);
        if (stream == null)
        {
            throw new InvalidOperationException($"Could not load embedded resource: {resourceName}");
        }

        using var reader = new StreamReader(stream);
        return await reader.ReadToEndAsync();
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
