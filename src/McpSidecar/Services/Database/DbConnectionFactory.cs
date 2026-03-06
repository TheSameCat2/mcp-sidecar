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
                // Check schema version for potential migrations
                var currentVersion = await GetSchemaVersionAsync(connection, cancellationToken);
                _logger.LogDebug("SQLite schema version: {Version}", currentVersion);
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

    /// <summary>
    /// Loads the SQLite schema from embedded resource.
    /// </summary>
    private static async Task<string> LoadEmbeddedSchemaAsync(CancellationToken cancellationToken)
    {
        var assembly = Assembly.GetExecutingAssembly();
        var resourceName = "McpSidecar.Schema.schema-sqlite.sql";

        await using var stream = assembly.GetManifestResourceStream(resourceName);
        if (stream == null)
        {
            // List available resources for debugging
            var availableResources = assembly.GetManifestResourceNames();
            var availableList = string.Join(", ", availableResources);
            throw new InvalidOperationException($"Embedded resource not found: {resourceName}. Available: {availableList}");
        }

        using var reader = new StreamReader(stream);
        return await reader.ReadToEndAsync(cancellationToken);
    }

    /// <summary>
    /// Gets the current schema version from the database.
    /// </summary>
    private static async Task<int> GetSchemaVersionAsync(SqliteConnection connection, CancellationToken cancellationToken)
    {
        try
        {
            await using var command = connection.CreateCommand();
            command.CommandText = "SELECT MAX(version) FROM schema_version;";
            var result = await command.ExecuteScalarAsync(cancellationToken);
            return result == null || result == DBNull.Value ? 0 : Convert.ToInt32(result);
        }
        catch (SqliteException)
        {
            // schema_version table doesn't exist yet
            return 0;
        }
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
