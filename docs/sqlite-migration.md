# SQLite Migration Plan

## Overview

Migrate McpSidecar from PostgreSQL (required) to SQLite (default) with optional PostgreSQL for power users.

## Goals
- **Default experience**: Single-file database, zero config, just works
- **Optional PostgreSQL**: For teams wanting shared indexing and advanced analytics
- **No functionality loss**: All tools work the same way

## Implementation Steps

### Phase 1: Infrastructure (Week 1)

#### 1. Add NuGet packages
```bash
cd src/McpSidecar
dotnet add package Microsoft.Data.Sqlite
```

#### 2. Create database abstraction layer
- Create `Services/Database/` directory
- `IDbConnectionFactory.cs` - creates appropriate connection based on config
- `IDbConnectionExtensions.cs` - extension methods that work with both backends
- `ISqlBuilder.cs` - handles SQL dialect differences

#### 3. Update connection configuration
```json
// appsettings.json
{
  "Database": {
    "Provider": "sqlite",  // or "postgres"
    "Sqlite": {
      "DatabasePath": "~/.local/share/mcp-sidecar/{workspace_hash}.db"
    },
    "Postgres": {
      "ConnectionString": "Host=localhost;Port=5432;..."
    }
  }
}
```

### Phase 2: Schema & Migrations (Week 1-2)

#### 1. Create SQLite schema
- File: `Schema/schema-sqlite.sql`
- Simplified from Postgres version (no partitions, simpler types)
- Precomputed tables as regular tables (symbol_card, call_rollup, etc.)

#### 2. Create SQL dialect helpers
```csharp
// Example: JSON handling differs between backends
public static class SqlDialect
{
    public static string JsonExtract(string column, string path)
        => _provider switch
        {
            "sqlite" => $"json_extract({column}, '$.path')",
            "postgres" => $"({column})->>'{path}'",
            _ => throw new NotSupportedException()
        };

    public static string JsonArrayLength(string column)
        => _provider switch
        {
            "sqlite" => $"json_array_length({column})",
            "postgres" => $"jsonb_array_length({column})",
            _ => throw new NotSupportedException()
        };

    public static string CurrentTimestamp => _provider switch
        {
            "sqlite" => "strftime('%s', 'now')",
            "postgres" => "now()",
            _ => throw new NotSupportedException()
        };
}
```

#### 3. Update all SQL queries
- Replace Postgres-specific syntax with dialect-agnostic calls
- Example: `COUNT(*) FILTER (WHERE ...)` becomes `SUM(CASE WHEN ... THEN 1 ELSE 0 END)`

### Phase 3: Data Access Layer (Week 2)

#### 1. Update ExtractionService
- Inject `IDbConnectionFactory`
- Use `ISqlBuilder` for dialect differences
- Remove direct `Npgsql` references

#### 2. Update McpServer tool implementations
- All Postgres queries go through abstraction layer
- Test with both backends

### Phase 4: Testing & Validation (Week 3)

#### 1. Unit tests
- Test SQLite schema creation
- Test all tools with SQLite backend
- Test migration from existing Postgres data

#### 2. Integration tests
- Full tool suite against both backend
- Performance benchmarks

#### 3. Migration tool
- Create `mcp-sidecar migrate` command
- Migrates existing Postgres data to SQLite
- Validates data integrity

## SQL Dialect Differences to Handle

| Feature | PostgreSQL | SQLite |
|---------|-----------|--------|
| JSON extract | `col->>'path'` | `json_extract(col, '$.path')` |
| JSON array length | `jsonb_array_length(col)` | `json_array_length(col)` |
| Current timestamp | `now()` | `strftime('%s', 'now')` |
| Boolean type | `boolean` | `integer` (0/1) |
| Filter in aggregate | `COUNT(*) FILTER (WHERE x)` | `SUM(CASE WHEN x THEN 1 ELSE 0 END)` |
| Array agg | `ARRAY_AGG(x)` | `json_group_array(x)` |
| Lateral join | `LEFT JOIN LATERAL (...)` | Subquery in SELECT or CTE |

## Precomputed Tables Refresh Strategy

Instead of materialized views, use regular tables with explicit refresh:

```csharp
public class RollupRefreshService
{
    public async Task RefreshSymbolCardsAsync(long snapshotId)
    {
        // Clear existing cards
        await _db.ExecuteAsync("DELETE FROM symbol_card WHERE snapshot_id = @snapshotId");

        // Rebuild from base tables (same logic as Postgres MV)
        await _db.ExecuteAsync(@"
            INSERT INTO symbol_card (...)
            SELECT ... -- same query as before
        ");
    }

    // Call after extraction completes
    public async Task OnExtractionCompleteAsync()
    {
        await RefreshSymbolCardsAsync(_currentSnapshotId);
        await RefreshCallRollupsAsync(_currentSnapshotId);
        // ... etc
    }
}
```

## Packaging & Distribution

### Default (SQLite)
``bash
# Users just need .NET runtime and clangd
dotnet tool install --global mcp-sidecar
mcp-sidecar --workspace /path/to/project
```

### Optional Postgres
```bash
# Docker compose with postgres
docker-compose up -d
# Or configure connection string
export MCP_POSTGRES_CONNECTION="Host=prod-db;..."
mcp-sidecar --workspace /path/to/project
```

## Backward Compatibility

- Existing Postgres deployments continue to work unchanged
- New `Database:Provider` config defaults to `sqlite` if not set
- If `MCP_POSTGRES_CONNECTION` env var is set, uses Postgres (backward compat)
- Migration tool helps move data to SQLite

## Timeline Estimate

| Phase | Task | Effort |
|------|------|--------|
| 1 | NuGet packages, schema | 1 day |
| 1 | Database abstraction layer | 2 days |
| 2 | Update all SQL queries | 2 days |
| 3 | Testing & validation | 2 days |
| **Total** | | **~1.5 weeks** |

## Success Criteria

- [ ] Fresh install works with just `dotnet tool install`
- [ ] All tools pass tests with SQLite backend
- [ ] Existing Postgres users unaffected
- [ ] Can migrate data from Postgres to SQLite
- [ ] Performance within 2x of single-user SQLite vs Postgres
