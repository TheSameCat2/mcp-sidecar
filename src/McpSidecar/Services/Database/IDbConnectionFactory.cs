using System.Data.Common;

namespace McpSidecar.Services.Database;

public interface IDbConnectionFactory
{
    string Provider { get; }
    bool IsConfigured { get; }
    Task<DbConnection> OpenConnectionAsync(CancellationToken cancellationToken = default);
}
