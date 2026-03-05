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
        normalized = RewriteFilterClause(normalized);
        normalized = RewriteAnyOperator(normalized);
        normalized = RewriteUnnest(normalized);
        normalized = normalized.Replace("jsonb_typeof(", "json_type(", StringComparison.OrdinalIgnoreCase);
        normalized = normalized.Replace("jsonb_array_length(", "json_array_length(", StringComparison.OrdinalIgnoreCase);
        normalized = normalized.Replace("jsonb_", "json_", StringComparison.OrdinalIgnoreCase);
        normalized = NowRegex().Replace(normalized, "CURRENT_TIMESTAMP");
        normalized = ArrayAggRegex().Replace(normalized, "json_group_array(");
        normalized = NullsLastRegex().Replace(normalized, string.Empty);
        normalized = normalized.Replace("ARRAY[]", "'[]'", StringComparison.OrdinalIgnoreCase);
        normalized = normalized.Replace("'{}'::jsonb", "'{}'", StringComparison.OrdinalIgnoreCase);
        normalized = normalized.Replace("'[]'::jsonb", "'[]'", StringComparison.OrdinalIgnoreCase);
        return normalized;
    }

    private static string RewriteFilterClause(string sql)
    {
        // Convert PostgreSQL FILTER clauses to SQLite compatible form:
        // 1. COUNT(*) FILTER (WHERE condition) -> SUM(CASE WHEN condition THEN 1 ELSE 0 END)
        // 2. COUNT(*) FILTER (condition) -> SUM(CASE WHEN condition THEN 1 ELSE 0 END)
        // Handle nested parentheses by finding matching parens
        var result = new System.Text.StringBuilder();
        int i = 0;
        while (i < sql.Length)
        {
            // Look for COUNT(*) FILTER ( - case insensitive
            var filterIdx = sql.IndexOf("COUNT(*) FILTER (", i, StringComparison.OrdinalIgnoreCase);
            if (filterIdx != i)
            {
                // No match at current position, copy character and move on
                result.Append(sql[i]);
                i++;
                continue;
            }
            
            // Found COUNT(*) FILTER ( at position i
            i += "COUNT(*) FILTER (".Length;
            
            // Skip whitespace
            while (i < sql.Length && char.IsWhiteSpace(sql[i])) i++;
            
            // Check if there's a WHERE keyword
            bool hasWhere = false;
            if (i + 5 <= sql.Length && sql.Substring(i, 5).Equals("WHERE", StringComparison.OrdinalIgnoreCase))
            {
                hasWhere = true;
                i += 5;
                // Skip whitespace after WHERE
                while (i < sql.Length && char.IsWhiteSpace(sql[i])) i++;
            }
            
            // Now we're at the start of the condition
            // Find matching closing paren (depth starts at 1 for the FILTER paren)
            int depth = 1;
            int start = i;
            while (i < sql.Length && depth > 0)
            {
                if (sql[i] == '(') depth++;
                else if (sql[i] == ')') depth--;
                if (depth > 0) i++;
            }
            
            var condition = sql[start..i];
            i++; // Move past the closing paren
            
            result.Append($"SUM(CASE WHEN {condition} THEN 1 ELSE 0 END)");
        }
        return result.ToString();
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
