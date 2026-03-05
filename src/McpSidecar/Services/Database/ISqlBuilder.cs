namespace McpSidecar.Services.Database;

public interface ISqlBuilder
{
    string Provider { get; }
    bool IsSqlite { get; }
    string Normalize(string sql);
}
