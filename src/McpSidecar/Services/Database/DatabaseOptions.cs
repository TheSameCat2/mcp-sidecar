namespace McpSidecar.Services.Database;

public sealed class DatabaseOptions
{
    public SqliteDatabaseOptions Sqlite { get; set; } = new();
}

public sealed class SqliteDatabaseOptions
{
    public string DatabasePath { get; set; } = "~/.local/share/mcp-sidecar/{workspace_hash}.db";
}
