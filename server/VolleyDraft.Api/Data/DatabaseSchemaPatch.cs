using System.Data;
using Microsoft.EntityFrameworkCore;

namespace VolleyDraft.Api.Data;

public static class DatabaseSchemaPatch
{
    public static async Task EnsureLatestAsync(VolleyDraftDbContext db)
    {
        var provider = db.Database.ProviderName ?? string.Empty;
        if (provider.Contains("Sqlite", StringComparison.OrdinalIgnoreCase))
        {
            await EnsureSqliteColumn(db, "MatchSessions", "CreatedAt", "\"CreatedAt\" TEXT NOT NULL DEFAULT '1970-01-01T00:00:00+00:00'");
            await EnsureSqliteColumn(db, "MatchSessions", "UpdatedAt", "\"UpdatedAt\" TEXT NOT NULL DEFAULT '1970-01-01T00:00:00+00:00'");
            await EnsureSqliteColumn(db, "SessionPlayers", "Gender", "\"Gender\" TEXT NOT NULL DEFAULT 'Male'");
            await EnsureSqliteColumn(db, "DraftSlots", "Gender", "\"Gender\" TEXT NOT NULL DEFAULT 'Male'");
            return;
        }

        if (provider.Contains("Npgsql", StringComparison.OrdinalIgnoreCase))
        {
            await EnsurePostgresColumn(db, "MatchSessions", "CreatedAt", "\"CreatedAt\" timestamp with time zone NOT NULL DEFAULT CURRENT_TIMESTAMP");
            await EnsurePostgresColumn(db, "MatchSessions", "UpdatedAt", "\"UpdatedAt\" timestamp with time zone NOT NULL DEFAULT CURRENT_TIMESTAMP");
            await EnsurePostgresColumn(db, "SessionPlayers", "Gender", "\"Gender\" text NOT NULL DEFAULT 'Male'");
            await EnsurePostgresColumn(db, "DraftSlots", "Gender", "\"Gender\" text NOT NULL DEFAULT 'Male'");
        }
    }

    private static async Task EnsureSqliteColumn(
        VolleyDraftDbContext db,
        string tableName,
        string columnName,
        string columnDefinition)
    {
        await using var command = db.Database.GetDbConnection().CreateCommand();
        command.CommandText = $"SELECT COUNT(*) FROM pragma_table_info('{tableName}') WHERE name = '{columnName}'";
        await OpenIfNeeded(db);
        var count = Convert.ToInt32(await command.ExecuteScalarAsync());

        if (count == 0)
        {
            var sql = "ALTER TABLE \"" + tableName + "\" ADD COLUMN " + columnDefinition;
            await db.Database.ExecuteSqlRawAsync(sql);
        }
    }

    private static async Task EnsurePostgresColumn(
        VolleyDraftDbContext db,
        string tableName,
        string columnName,
        string columnDefinition)
    {
        await using var command = db.Database.GetDbConnection().CreateCommand();
        command.CommandText = """
            SELECT COUNT(*)
            FROM information_schema.columns
            WHERE table_schema = current_schema()
              AND table_name = @tableName
              AND column_name = @columnName
            """;

        var tableParameter = command.CreateParameter();
        tableParameter.ParameterName = "tableName";
        tableParameter.Value = tableName;
        command.Parameters.Add(tableParameter);

        var columnParameter = command.CreateParameter();
        columnParameter.ParameterName = "columnName";
        columnParameter.Value = columnName;
        command.Parameters.Add(columnParameter);

        await OpenIfNeeded(db);
        var count = Convert.ToInt32(await command.ExecuteScalarAsync());

        if (count == 0)
        {
            var sql = "ALTER TABLE \"" + tableName + "\" ADD COLUMN " + columnDefinition;
            await db.Database.ExecuteSqlRawAsync(sql);
        }
    }

    private static async Task OpenIfNeeded(VolleyDraftDbContext db)
    {
        var connection = db.Database.GetDbConnection();
        if (connection.State != ConnectionState.Open)
        {
            await connection.OpenAsync();
        }
    }
}
