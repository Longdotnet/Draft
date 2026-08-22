using System.Data;
using Microsoft.EntityFrameworkCore;

namespace VolleyDraft.Api.Data;

public static class ZaloGuestReservationSchemaPatch
{
    private static readonly SemaphoreSlim Gate = new(1, 1);

    public static async Task EnsureAsync(
        VolleyDraftDbContext db,
        CancellationToken cancellationToken = default)
    {
        var provider = db.Database.ProviderName ?? string.Empty;
        var isPostgres = provider.Contains("Npgsql", StringComparison.OrdinalIgnoreCase);
        var isSqlite = provider.Contains("Sqlite", StringComparison.OrdinalIgnoreCase);
        if (!isPostgres && !isSqlite) return;

        await Gate.WaitAsync(cancellationToken);
        try
        {
            var sql = isPostgres
                ? """
                    CREATE TABLE IF NOT EXISTS "ZaloGuestReservations" (
                        "Id" TEXT NOT NULL CONSTRAINT "PK_ZaloGuestReservations" PRIMARY KEY,
                        "SessionId" TEXT NOT NULL,
                        "SessionPlayerId" TEXT NULL,
                        "SponsorZaloUserId" TEXT NOT NULL,
                        "SponsorDisplayName" TEXT NOT NULL,
                        "DisplayName" TEXT NOT NULL,
                        "GuestIndex" integer NOT NULL,
                        "SponsorSequence" integer NOT NULL,
                        "Gender" integer NULL,
                        "Role" integer NULL,
                        "Level" integer NULL,
                        "SourceMessageId" TEXT NOT NULL,
                        "RecruitmentMessageId" TEXT NULL,
                        "Status" integer NOT NULL,
                        "CreatedAt" timestamp with time zone NOT NULL,
                        "UpdatedAt" timestamp with time zone NOT NULL,
                        CONSTRAINT "FK_ZaloGuestReservations_MatchSessions_SessionId"
                            FOREIGN KEY ("SessionId") REFERENCES "MatchSessions" ("Id") ON DELETE CASCADE
                    );
                    CREATE UNIQUE INDEX IF NOT EXISTS "UX_ZaloGuestReservations_Source"
                    ON "ZaloGuestReservations" ("SessionId", "SourceMessageId", "GuestIndex");
                    CREATE INDEX IF NOT EXISTS "IX_ZaloGuestReservations_SponsorStatus"
                    ON "ZaloGuestReservations" ("SessionId", "SponsorZaloUserId", "Status", "CreatedAt");
                    CREATE INDEX IF NOT EXISTS "IX_ZaloGuestReservations_Waitlist"
                    ON "ZaloGuestReservations" ("SessionId", "Status", "CreatedAt");
                    """
                : """
                    CREATE TABLE IF NOT EXISTS "ZaloGuestReservations" (
                        "Id" TEXT NOT NULL CONSTRAINT "PK_ZaloGuestReservations" PRIMARY KEY,
                        "SessionId" TEXT NOT NULL,
                        "SessionPlayerId" TEXT NULL,
                        "SponsorZaloUserId" TEXT NOT NULL,
                        "SponsorDisplayName" TEXT NOT NULL,
                        "DisplayName" TEXT NOT NULL,
                        "GuestIndex" INTEGER NOT NULL,
                        "SponsorSequence" INTEGER NOT NULL,
                        "Gender" INTEGER NULL,
                        "Role" INTEGER NULL,
                        "Level" INTEGER NULL,
                        "SourceMessageId" TEXT NOT NULL,
                        "RecruitmentMessageId" TEXT NULL,
                        "Status" INTEGER NOT NULL,
                        "CreatedAt" TEXT NOT NULL,
                        "UpdatedAt" TEXT NOT NULL,
                        CONSTRAINT "FK_ZaloGuestReservations_MatchSessions_SessionId"
                            FOREIGN KEY ("SessionId") REFERENCES "MatchSessions" ("Id") ON DELETE CASCADE
                    );
                    CREATE UNIQUE INDEX IF NOT EXISTS "UX_ZaloGuestReservations_Source"
                    ON "ZaloGuestReservations" ("SessionId", "SourceMessageId", "GuestIndex");
                    CREATE INDEX IF NOT EXISTS "IX_ZaloGuestReservations_SponsorStatus"
                    ON "ZaloGuestReservations" ("SessionId", "SponsorZaloUserId", "Status", "CreatedAt");
                    CREATE INDEX IF NOT EXISTS "IX_ZaloGuestReservations_Waitlist"
                    ON "ZaloGuestReservations" ("SessionId", "Status", "CreatedAt");
                    """;
            await db.Database.ExecuteSqlRawAsync(sql, cancellationToken);

            if (isPostgres)
            {
                await db.Database.ExecuteSqlRawAsync(
                    """
                    ALTER TABLE "ZaloGuestReservations" ADD COLUMN IF NOT EXISTS "Role" integer NULL;
                    ALTER TABLE "ZaloGuestReservations" ADD COLUMN IF NOT EXISTS "Level" integer NULL;
                    """,
                    cancellationToken);
            }
            else
            {
                await EnsureSqliteColumnAsync(db, "Role", "INTEGER NULL", cancellationToken);
                await EnsureSqliteColumnAsync(db, "Level", "INTEGER NULL", cancellationToken);
            }
        }
        finally
        {
            Gate.Release();
        }
    }

    private static async Task EnsureSqliteColumnAsync(
        VolleyDraftDbContext db,
        string columnName,
        string definition,
        CancellationToken cancellationToken)
    {
        var connection = db.Database.GetDbConnection();
        if (connection.State != ConnectionState.Open)
            await connection.OpenAsync(cancellationToken);

        var exists = false;
        await using (var query = connection.CreateCommand())
        {
            query.CommandText = "PRAGMA table_info(\"ZaloGuestReservations\");";
            await using var reader = await query.ExecuteReaderAsync(cancellationToken);
            while (await reader.ReadAsync(cancellationToken))
            {
                if (string.Equals(Convert.ToString(reader.GetValue(1)), columnName, StringComparison.OrdinalIgnoreCase))
                {
                    exists = true;
                    break;
                }
            }
        }
        if (exists) return;

        await using var alter = connection.CreateCommand();
        alter.CommandText = $"ALTER TABLE \"ZaloGuestReservations\" ADD COLUMN \"{columnName}\" {definition};";
        await alter.ExecuteNonQueryAsync(cancellationToken);
    }
}
