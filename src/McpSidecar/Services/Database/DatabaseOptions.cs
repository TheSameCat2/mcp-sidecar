namespace McpSidecar.Services.Database;

public sealed class DatabaseOptions
{
    public string? Provider { get; set; }
    public SqliteDatabaseOptions Sqlite { get; set; } = new();
    public PostgresDatabaseOptions Postgres { get; set; } = new();
}

public sealed class SqliteDatabaseOptions
{
    public string DatabasePath { get; set; } = "~/.local/share/mcp-sidecar/{workspace_hash}.db";
}

public sealed class PostgresDatabaseOptions
{
    public string? ConnectionString { get; set; }
}
