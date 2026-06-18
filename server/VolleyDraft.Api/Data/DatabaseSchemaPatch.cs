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
            await EnsureSqliteColumn(db, "BlindBags", "PreparedDraftSlotId", "\"PreparedDraftSlotId\" TEXT NULL");
            await EnsureSqliteTeamPreferenceTables(db);
            return;
        }

        if (provider.Contains("Npgsql", StringComparison.OrdinalIgnoreCase))
        {
            await EnsurePostgresColumn(db, "MatchSessions", "CreatedAt", "\"CreatedAt\" timestamp with time zone NOT NULL DEFAULT CURRENT_TIMESTAMP");
            await EnsurePostgresColumn(db, "MatchSessions", "UpdatedAt", "\"UpdatedAt\" timestamp with time zone NOT NULL DEFAULT CURRENT_TIMESTAMP");
            await EnsurePostgresColumn(db, "SessionPlayers", "Gender", "\"Gender\" text NOT NULL DEFAULT 'Male'");
            await EnsurePostgresColumn(db, "DraftSlots", "Gender", "\"Gender\" text NOT NULL DEFAULT 'Male'");
            await EnsurePostgresColumn(db, "BlindBags", "PreparedDraftSlotId", "\"PreparedDraftSlotId\" text NULL");
            await EnsurePostgresTeamPreferenceTables(db);
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

    private static async Task EnsureSqliteTeamPreferenceTables(VolleyDraftDbContext db)
    {
        await db.Database.ExecuteSqlRawAsync("""
            CREATE TABLE IF NOT EXISTS "TeamPreferenceGroups" (
                "Id" TEXT NOT NULL CONSTRAINT "PK_TeamPreferenceGroups" PRIMARY KEY,
                "SessionId" TEXT NOT NULL,
                "CreatedAt" TEXT NOT NULL DEFAULT '1970-01-01T00:00:00+00:00',
                CONSTRAINT "FK_TeamPreferenceGroups_MatchSessions_SessionId"
                    FOREIGN KEY ("SessionId") REFERENCES "MatchSessions" ("Id") ON DELETE CASCADE
            );
            """);
        await db.Database.ExecuteSqlRawAsync("""
            CREATE TABLE IF NOT EXISTS "TeamPreferenceGroupPlayers" (
                "TeamPreferenceGroupId" TEXT NOT NULL,
                "SessionPlayerId" TEXT NOT NULL,
                "RotationOrder" INTEGER NOT NULL,
                CONSTRAINT "PK_TeamPreferenceGroupPlayers"
                    PRIMARY KEY ("TeamPreferenceGroupId", "SessionPlayerId"),
                CONSTRAINT "FK_TeamPreferenceGroupPlayers_TeamPreferenceGroups_TeamPreferenceGroupId"
                    FOREIGN KEY ("TeamPreferenceGroupId") REFERENCES "TeamPreferenceGroups" ("Id") ON DELETE CASCADE,
                CONSTRAINT "FK_TeamPreferenceGroupPlayers_SessionPlayers_SessionPlayerId"
                    FOREIGN KEY ("SessionPlayerId") REFERENCES "SessionPlayers" ("Id") ON DELETE RESTRICT
            );
            """);
        await db.Database.ExecuteSqlRawAsync(
            """CREATE INDEX IF NOT EXISTS "IX_TeamPreferenceGroups_SessionId" ON "TeamPreferenceGroups" ("SessionId");""");
        await db.Database.ExecuteSqlRawAsync(
            """CREATE UNIQUE INDEX IF NOT EXISTS "IX_TeamPreferenceGroupPlayers_SessionPlayerId" ON "TeamPreferenceGroupPlayers" ("SessionPlayerId");""");
    }

    private static async Task EnsurePostgresTeamPreferenceTables(VolleyDraftDbContext db)
    {
        await db.Database.ExecuteSqlRawAsync("""
            CREATE TABLE IF NOT EXISTS "TeamPreferenceGroups" (
                "Id" text NOT NULL CONSTRAINT "PK_TeamPreferenceGroups" PRIMARY KEY,
                "SessionId" text NOT NULL,
                "CreatedAt" timestamp with time zone NOT NULL DEFAULT CURRENT_TIMESTAMP,
                CONSTRAINT "FK_TeamPreferenceGroups_MatchSessions_SessionId"
                    FOREIGN KEY ("SessionId") REFERENCES "MatchSessions" ("Id") ON DELETE CASCADE
            );
            """);
        await db.Database.ExecuteSqlRawAsync("""
            CREATE TABLE IF NOT EXISTS "TeamPreferenceGroupPlayers" (
                "TeamPreferenceGroupId" text NOT NULL,
                "SessionPlayerId" text NOT NULL,
                "RotationOrder" integer NOT NULL,
                CONSTRAINT "PK_TeamPreferenceGroupPlayers"
                    PRIMARY KEY ("TeamPreferenceGroupId", "SessionPlayerId"),
                CONSTRAINT "FK_TeamPreferenceGroupPlayers_TeamPreferenceGroups_TeamPreferenceGroupId"
                    FOREIGN KEY ("TeamPreferenceGroupId") REFERENCES "TeamPreferenceGroups" ("Id") ON DELETE CASCADE,
                CONSTRAINT "FK_TeamPreferenceGroupPlayers_SessionPlayers_SessionPlayerId"
                    FOREIGN KEY ("SessionPlayerId") REFERENCES "SessionPlayers" ("Id") ON DELETE RESTRICT
            );
            """);
        await db.Database.ExecuteSqlRawAsync(
            """CREATE INDEX IF NOT EXISTS "IX_TeamPreferenceGroups_SessionId" ON "TeamPreferenceGroups" ("SessionId");""");
        await db.Database.ExecuteSqlRawAsync(
            """CREATE UNIQUE INDEX IF NOT EXISTS "IX_TeamPreferenceGroupPlayers_SessionPlayerId" ON "TeamPreferenceGroupPlayers" ("SessionPlayerId");""");
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
