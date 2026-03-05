using System.Text.RegularExpressions;

namespace McpSidecar.Services.Database;

public sealed partial class SqlBuilder : ISqlBuilder
{
    public SqlBuilder(IDbConnectionFactory connectionFactory)
    {
        Provider = connectionFactory.Provider;
        IsSqlite = string.Equals(Provider, "sqlite", StringComparison.OrdinalIgnoreCase);
    }

    public string Provider { get; }
    public bool IsSqlite { get; }

    public string Normalize(string sql)
    {
        if (!IsSqlite || string.IsNullOrWhiteSpace(sql))
        {
            return sql;
        }

        var normalized = sql;
        normalized = CastRegex().Replace(normalized, string.Empty);
        normalized = RewriteAnyOperator(normalized);
        normalized = RewriteUnnest(normalized);
        normalized = normalized.Replace("jsonb_typeof(", "json_type(", StringComparison.OrdinalIgnoreCase);
        normalized = normalized.Replace("jsonb_array_length(", "json_array_length(", StringComparison.OrdinalIgnoreCase);
        normalized = NowRegex().Replace(normalized, "CURRENT_TIMESTAMP");
        normalized = ArrayAggRegex().Replace(normalized, "json_group_array(");
        normalized = NullsLastRegex().Replace(normalized, string.Empty);
        normalized = normalized.Replace("ARRAY[]", "'[]'", StringComparison.OrdinalIgnoreCase);
        return normalized;
    }

    private static string RewriteAnyOperator(string sql)
    {
        return AnyOperatorRegex().Replace(sql, match =>
        {
            var left = match.Groups["left"].Value;
            var parameter = match.Groups["param"].Value;
            var alias = $"any_{parameter}";
            return $"EXISTS (SELECT 1 FROM json_each(@{parameter}) {alias} WHERE CAST({alias}.value AS INTEGER) = {left})";
        });
    }

    private static string RewriteUnnest(string sql)
    {
        return UnnestRegex().Replace(
            sql,
            "SELECT DISTINCT CAST(value AS INTEGER) AS ${alias} FROM json_each(@${param})");
    }

    [GeneratedRegex(
        @"(?<left>[A-Za-z_][A-Za-z0-9_\.]*)\s*=\s*ANY\(\s*@(?<param>[A-Za-z_][A-Za-z0-9_]*)\s*\)",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex AnyOperatorRegex();

    [GeneratedRegex(
        @"SELECT\s+DISTINCT\s+unnest\(\s*@(?<param>[A-Za-z_][A-Za-z0-9_]*)\s*\)\s+AS\s+(?<alias>[A-Za-z_][A-Za-z0-9_]*)",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex UnnestRegex();

    [GeneratedRegex(@"\bnow\(\)", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex NowRegex();

    [GeneratedRegex(@"\bARRAY_AGG\s*\(", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex ArrayAggRegex();

    [GeneratedRegex(@"\bNULLS\s+LAST\b", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex NullsLastRegex();

    [GeneratedRegex(
        @"::\s*[A-Za-z_][A-Za-z0-9_]*(?:\s*\([^)]*\))?(?:\[\])?",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex CastRegex();
}
