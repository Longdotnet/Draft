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
                        "SourceMessageId" TEXT NOT NULL,
                        "RecruitmentMessageId" TEXT NULL,
                        "Status" integer NOT NULL,
                        "CreatedAt" timestamp with time zone NOT NULL,
                        "UpdatedAt" timestamp with time zone NOT NULL
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
                        "SourceMessageId" TEXT NOT NULL,
                        "RecruitmentMessageId" TEXT NULL,
                        "Status" INTEGER NOT NULL,
                        "CreatedAt" TEXT NOT NULL,
                        "UpdatedAt" TEXT NOT NULL
                    );
                    CREATE UNIQUE INDEX IF NOT EXISTS "UX_ZaloGuestReservations_Source"
                    ON "ZaloGuestReservations" ("SessionId", "SourceMessageId", "GuestIndex");
                    CREATE INDEX IF NOT EXISTS "IX_ZaloGuestReservations_SponsorStatus"
                    ON "ZaloGuestReservations" ("SessionId", "SponsorZaloUserId", "Status", "CreatedAt");
                    CREATE INDEX IF NOT EXISTS "IX_ZaloGuestReservations_Waitlist"
                    ON "ZaloGuestReservations" ("SessionId", "Status", "CreatedAt");
                    """;
            await db.Database.ExecuteSqlRawAsync(sql, cancellationToken);
        }
        finally
        {
            Gate.Release();
        }
    }
}
