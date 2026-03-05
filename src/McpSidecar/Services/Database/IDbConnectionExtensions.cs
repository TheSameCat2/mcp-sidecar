using System.Data.Common;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using Npgsql;
using NpgsqlTypes;

namespace McpSidecar.Services.Database;

public static class IDbConnectionExtensions
{
    public static DbCommand CreateDbCommand(this DbConnection connection, ISqlBuilder sqlBuilder, string sql)
    {
        var cmd = connection.CreateCommand();
        cmd.CommandText = sqlBuilder.Normalize(sql);
        return cmd;
    }

    public static DbParameter AddWithValue(this DbParameterCollection parameters, string parameterName, object? value)
    {
        var normalizedValue = NormalizeParameterValue(parameters, value);

        if (parameters is NpgsqlParameterCollection npgsqlParameters)
        {
            return npgsqlParameters.AddWithValue(parameterName, normalizedValue);
        }

        if (parameters is SqliteParameterCollection sqliteParameters)
        {
            var effectiveName = parameterName.StartsWith('@') ? parameterName : $"@{parameterName}";
            return sqliteParameters.AddWithValue(effectiveName, normalizedValue);
        }

        throw new NotSupportedException($"Unsupported parameter collection type: {parameters.GetType().FullName}");
    }

    public static DbParameter AddWithValue(this DbParameterCollection parameters, string parameterName, NpgsqlDbType dbType, object? value)
    {
        if (parameters is NpgsqlParameterCollection npgsqlParameters)
        {
            return npgsqlParameters.AddWithValue(parameterName, dbType, value ?? DBNull.Value);
        }

        return parameters.AddWithValue(parameterName, value);
    }

    private static object NormalizeParameterValue(DbParameterCollection parameters, object? value)
    {
        if (value == null)
        {
            return DBNull.Value;
        }

        if (parameters is not SqliteParameterCollection)
        {
            return value;
        }

        return value switch
        {
            bool flag => flag ? 1 : 0,
            DateTimeOffset dto => dto.UtcDateTime.ToString("O"),
            decimal d => Convert.ToDouble(d),
            Array array when value is not byte[] => JsonSerializer.Serialize(array),
            _ => value
        };
    }
}
