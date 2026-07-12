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
            await EnsureSqliteColumn(db, "MatchSessions", "ZaloConnectionId", "\"ZaloConnectionId\" TEXT NULL");
            await EnsureSqliteColumn(db, "MatchSessions", "ZaloGroupId", "\"ZaloGroupId\" TEXT NULL");
            await EnsureSqliteColumn(db, "MatchSessions", "ZaloGroupName", "\"ZaloGroupName\" TEXT NULL");
            await EnsureSqliteColumn(db, "MatchSessions", "ZaloGroupAvatarUrl", "\"ZaloGroupAvatarUrl\" TEXT NULL");
            await EnsureSqliteColumn(db, "MatchSessions", "StartTime", "\"StartTime\" TEXT NULL");
            await EnsureSqliteColumn(db, "MatchSessions", "Location", "\"Location\" TEXT NULL");
            await EnsureSqliteColumn(db, "MatchSessions", "ParkingInstructions", "\"ParkingInstructions\" TEXT NULL");
            await EnsureSqliteColumn(db, "MatchSessions", "LocationImageUrl", "\"LocationImageUrl\" TEXT NULL");
            await EnsureSqliteColumn(db, "MatchSessions", "PaymentInstructions", "\"PaymentInstructions\" TEXT NULL");
            await EnsureSqliteColumn(db, "MatchSessions", "PaymentQrImageUrl", "\"PaymentQrImageUrl\" TEXT NULL");
            await EnsureSqliteColumn(db, "MatchSessions", "BotEnabled", "\"BotEnabled\" INTEGER NOT NULL DEFAULT 0");
            await EnsureSqliteColumn(db, "MatchSessions", "BotCustomInstructions", "\"BotCustomInstructions\" TEXT NULL");
            await EnsureSqliteColumn(db, "MatchSessions", "ReminderEnabled", "\"ReminderEnabled\" INTEGER NOT NULL DEFAULT 0");
            await EnsureSqliteColumn(db, "MatchSessions", "ReminderLeadHours", "\"ReminderLeadHours\" INTEGER NOT NULL DEFAULT 72");
            await EnsureSqliteColumn(db, "MatchSessions", "ReminderIntervalHours", "\"ReminderIntervalHours\" INTEGER NOT NULL DEFAULT 12");
            await EnsureSqliteColumn(db, "MatchSessions", "LastReminderAt", "\"LastReminderAt\" TEXT NULL");
            await EnsureSqliteColumn(db, "SessionPlayers", "Gender", "\"Gender\" TEXT NOT NULL DEFAULT 'Male'");
            await EnsureSqliteColumn(db, "SessionPlayers", "PlayerProfileId", "\"PlayerProfileId\" TEXT NULL");
            await EnsureSqliteColumn(db, "SessionPlayers", "AvatarUrl", "\"AvatarUrl\" TEXT NULL");
            await EnsureSqliteColumn(db, "SessionPlayers", "SourcePollId", "\"SourcePollId\" TEXT NULL");
            await EnsureSqliteColumn(db, "SessionPlayers", "SourceOptionIdsJson", "\"SourceOptionIdsJson\" TEXT NULL");
            await EnsureSqliteColumn(db, "DraftSlots", "Gender", "\"Gender\" TEXT NOT NULL DEFAULT 'Male'");
            await EnsureSqliteColumn(db, "BlindBags", "PreparedDraftSlotId", "\"PreparedDraftSlotId\" TEXT NULL");
            await EnsureSqliteTeamPreferenceTables(db);
            await EnsureSqliteZaloTables(db);
            await EnsureSqliteZaloBotTables(db);
            await EnsureSqliteZaloBotLearningTables(db);
            await EnsureSqliteZaloBotImageTables(db);
            await EnsureSqliteColumn(db, "ZaloBotImageAssets", "Size", "\"Size\" INTEGER NOT NULL DEFAULT 0");
            await EnsureSqliteColumn(db, "ZaloGroupMessages", "ReplyAttemptCount", "\"ReplyAttemptCount\" INTEGER NOT NULL DEFAULT 0");
            await EnsureSqliteColumn(db, "ZaloGroupMessages", "BotReplySentAt", "\"BotReplySentAt\" TEXT NULL");
            return;
        }

        if (provider.Contains("Npgsql", StringComparison.OrdinalIgnoreCase))
        {
            await EnsurePostgresColumn(db, "MatchSessions", "CreatedAt", "\"CreatedAt\" timestamp with time zone NOT NULL DEFAULT CURRENT_TIMESTAMP");
            await EnsurePostgresColumn(db, "MatchSessions", "UpdatedAt", "\"UpdatedAt\" timestamp with time zone NOT NULL DEFAULT CURRENT_TIMESTAMP");
            await EnsurePostgresColumn(db, "MatchSessions", "ZaloConnectionId", "\"ZaloConnectionId\" text NULL");
            await EnsurePostgresColumn(db, "MatchSessions", "ZaloGroupId", "\"ZaloGroupId\" text NULL");
            await EnsurePostgresColumn(db, "MatchSessions", "ZaloGroupName", "\"ZaloGroupName\" text NULL");
            await EnsurePostgresColumn(db, "MatchSessions", "ZaloGroupAvatarUrl", "\"ZaloGroupAvatarUrl\" text NULL");
            await EnsurePostgresColumn(db, "MatchSessions", "StartTime", "\"StartTime\" timestamp with time zone NULL");
            await EnsurePostgresColumn(db, "MatchSessions", "Location", "\"Location\" text NULL");
            await EnsurePostgresColumn(db, "MatchSessions", "ParkingInstructions", "\"ParkingInstructions\" text NULL");
            await EnsurePostgresColumn(db, "MatchSessions", "LocationImageUrl", "\"LocationImageUrl\" text NULL");
            await EnsurePostgresColumn(db, "MatchSessions", "PaymentInstructions", "\"PaymentInstructions\" text NULL");
            await EnsurePostgresColumn(db, "MatchSessions", "PaymentQrImageUrl", "\"PaymentQrImageUrl\" text NULL");
            await EnsurePostgresColumn(db, "MatchSessions", "BotEnabled", "\"BotEnabled\" boolean NOT NULL DEFAULT FALSE");
            await EnsurePostgresColumn(db, "MatchSessions", "BotCustomInstructions", "\"BotCustomInstructions\" text NULL");
            await EnsurePostgresColumn(db, "MatchSessions", "ReminderEnabled", "\"ReminderEnabled\" boolean NOT NULL DEFAULT FALSE");
            await EnsurePostgresColumn(db, "MatchSessions", "ReminderLeadHours", "\"ReminderLeadHours\" integer NOT NULL DEFAULT 72");
            await EnsurePostgresColumn(db, "MatchSessions", "ReminderIntervalHours", "\"ReminderIntervalHours\" integer NOT NULL DEFAULT 12");
            await EnsurePostgresColumn(db, "MatchSessions", "LastReminderAt", "\"LastReminderAt\" timestamp with time zone NULL");
            await EnsurePostgresColumn(db, "SessionPlayers", "Gender", "\"Gender\" text NOT NULL DEFAULT 'Male'");
            await EnsurePostgresColumn(db, "SessionPlayers", "PlayerProfileId", "\"PlayerProfileId\" text NULL");
            await EnsurePostgresColumn(db, "SessionPlayers", "AvatarUrl", "\"AvatarUrl\" text NULL");
            await EnsurePostgresColumn(db, "SessionPlayers", "SourcePollId", "\"SourcePollId\" text NULL");
            await EnsurePostgresColumn(db, "SessionPlayers", "SourceOptionIdsJson", "\"SourceOptionIdsJson\" text NULL");
            await EnsurePostgresColumn(db, "DraftSlots", "Gender", "\"Gender\" text NOT NULL DEFAULT 'Male'");
            await EnsurePostgresColumn(db, "BlindBags", "PreparedDraftSlotId", "\"PreparedDraftSlotId\" text NULL");
            await EnsurePostgresTeamPreferenceTables(db);
            await EnsurePostgresZaloTables(db);
            await EnsurePostgresZaloBotTables(db);
            await EnsurePostgresZaloBotLearningTables(db);
            await EnsurePostgresZaloBotImageTables(db);
            await EnsurePostgresColumn(db, "ZaloBotImageAssets", "Size", "\"Size\" bigint NOT NULL DEFAULT 0");
            await EnsurePostgresColumn(db, "ZaloGroupMessages", "ReplyAttemptCount", "\"ReplyAttemptCount\" integer NOT NULL DEFAULT 0");
            await EnsurePostgresColumn(db, "ZaloGroupMessages", "BotReplySentAt", "\"BotReplySentAt\" timestamp with time zone NULL");
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

    private static async Task EnsureSqliteZaloTables(VolleyDraftDbContext db)
    {
        await db.Database.ExecuteSqlRawAsync("""
            CREATE TABLE IF NOT EXISTS "PlayerProfiles" (
                "Id" TEXT NOT NULL CONSTRAINT "PK_PlayerProfiles" PRIMARY KEY,
                "ZaloUserId" TEXT NOT NULL,
                "DisplayName" TEXT NOT NULL,
                "AvatarUrl" TEXT NULL,
                "Gender" TEXT NULL,
                "DefaultRole" TEXT NULL,
                "DefaultLevel" TEXT NULL,
                "LastSyncedAt" TEXT NOT NULL,
                "GenderUpdatedAt" TEXT NULL,
                "GenderUpdatedByUserId" TEXT NULL,
                "CreatedAt" TEXT NOT NULL,
                "UpdatedAt" TEXT NOT NULL
            );
            """);
        await EnsureLegacySqlitePlayerProfileColumnDropped(db);
        await db.Database.ExecuteSqlRawAsync("""
            CREATE TABLE IF NOT EXISTS "ZaloConnections" (
                "Id" TEXT NOT NULL CONSTRAINT "PK_ZaloConnections" PRIMARY KEY,
                "AdminUserId" TEXT NOT NULL,
                "AccountZaloId" TEXT NOT NULL,
                "DisplayName" TEXT NOT NULL,
                "AvatarUrl" TEXT NULL,
                "EncryptedCredentials" TEXT NOT NULL,
                "Status" TEXT NOT NULL,
                "LastValidatedAt" TEXT NOT NULL,
                "CreatedAt" TEXT NOT NULL,
                "UpdatedAt" TEXT NOT NULL,
                CONSTRAINT "FK_ZaloConnections_Users_AdminUserId"
                    FOREIGN KEY ("AdminUserId") REFERENCES "Users" ("Id") ON DELETE CASCADE
            );
            """);
        await db.Database.ExecuteSqlRawAsync("""
            CREATE TABLE IF NOT EXISTS "PollImports" (
                "Id" TEXT NOT NULL CONSTRAINT "PK_PollImports" PRIMARY KEY,
                "SessionId" TEXT NOT NULL,
                "ImportedByUserId" TEXT NOT NULL,
                "ZaloGroupId" TEXT NOT NULL,
                "PollId" TEXT NOT NULL,
                "PollQuestion" TEXT NOT NULL,
                "SelectedOptionIdsJson" TEXT NOT NULL,
                "ImportedPlayerCount" INTEGER NOT NULL,
                "ImportedAt" TEXT NOT NULL,
                CONSTRAINT "FK_PollImports_MatchSessions_SessionId"
                    FOREIGN KEY ("SessionId") REFERENCES "MatchSessions" ("Id") ON DELETE CASCADE,
                CONSTRAINT "FK_PollImports_Users_ImportedByUserId"
                    FOREIGN KEY ("ImportedByUserId") REFERENCES "Users" ("Id") ON DELETE RESTRICT
            );
            """);
        await db.Database.ExecuteSqlRawAsync(
            """CREATE UNIQUE INDEX IF NOT EXISTS "IX_PlayerProfiles_ZaloUserId" ON "PlayerProfiles" ("ZaloUserId");""");
        await db.Database.ExecuteSqlRawAsync(
            """CREATE UNIQUE INDEX IF NOT EXISTS "IX_ZaloConnections_AdminUserId_AccountZaloId" ON "ZaloConnections" ("AdminUserId", "AccountZaloId");""");
        await db.Database.ExecuteSqlRawAsync(
            """CREATE INDEX IF NOT EXISTS "IX_ZaloConnections_AdminUserId" ON "ZaloConnections" ("AdminUserId");""");
        await db.Database.ExecuteSqlRawAsync(
            """CREATE INDEX IF NOT EXISTS "IX_PollImports_SessionId_PollId" ON "PollImports" ("SessionId", "PollId");""");
        await db.Database.ExecuteSqlRawAsync(
            """CREATE UNIQUE INDEX IF NOT EXISTS "IX_SessionPlayers_SessionId_PlayerProfileId" ON "SessionPlayers" ("SessionId", "PlayerProfileId") WHERE "PlayerProfileId" IS NOT NULL;""");
        await db.Database.ExecuteSqlRawAsync(
            """CREATE INDEX IF NOT EXISTS "IX_MatchSessions_ZaloConnectionId" ON "MatchSessions" ("ZaloConnectionId");""");
    }

    private static async Task EnsurePostgresZaloTables(VolleyDraftDbContext db)
    {
        await db.Database.ExecuteSqlRawAsync("""
            CREATE TABLE IF NOT EXISTS "PlayerProfiles" (
                "Id" text NOT NULL CONSTRAINT "PK_PlayerProfiles" PRIMARY KEY,
                "ZaloUserId" text NOT NULL,
                "DisplayName" text NOT NULL,
                "AvatarUrl" text NULL,
                "Gender" text NULL,
                "DefaultRole" text NULL,
                "DefaultLevel" text NULL,
                "LastSyncedAt" timestamp with time zone NOT NULL,
                "GenderUpdatedAt" timestamp with time zone NULL,
                "GenderUpdatedByUserId" text NULL,
                "CreatedAt" timestamp with time zone NOT NULL,
                "UpdatedAt" timestamp with time zone NOT NULL
            );
            """);
        await db.Database.ExecuteSqlRawAsync("""
            CREATE TABLE IF NOT EXISTS "ZaloConnections" (
                "Id" text NOT NULL CONSTRAINT "PK_ZaloConnections" PRIMARY KEY,
                "AdminUserId" text NOT NULL,
                "AccountZaloId" text NOT NULL,
                "DisplayName" text NOT NULL,
                "AvatarUrl" text NULL,
                "EncryptedCredentials" text NOT NULL,
                "Status" text NOT NULL,
                "LastValidatedAt" timestamp with time zone NOT NULL,
                "CreatedAt" timestamp with time zone NOT NULL,
                "UpdatedAt" timestamp with time zone NOT NULL,
                CONSTRAINT "FK_ZaloConnections_Users_AdminUserId"
                    FOREIGN KEY ("AdminUserId") REFERENCES "Users" ("Id") ON DELETE CASCADE
            );
            """);
        await db.Database.ExecuteSqlRawAsync("""
            CREATE TABLE IF NOT EXISTS "PollImports" (
                "Id" text NOT NULL CONSTRAINT "PK_PollImports" PRIMARY KEY,
                "SessionId" text NOT NULL,
                "ImportedByUserId" text NOT NULL,
                "ZaloGroupId" text NOT NULL,
                "PollId" text NOT NULL,
                "PollQuestion" text NOT NULL,
                "SelectedOptionIdsJson" text NOT NULL,
                "ImportedPlayerCount" integer NOT NULL,
                "ImportedAt" timestamp with time zone NOT NULL,
                CONSTRAINT "FK_PollImports_MatchSessions_SessionId"
                    FOREIGN KEY ("SessionId") REFERENCES "MatchSessions" ("Id") ON DELETE CASCADE,
                CONSTRAINT "FK_PollImports_Users_ImportedByUserId"
                    FOREIGN KEY ("ImportedByUserId") REFERENCES "Users" ("Id") ON DELETE RESTRICT
            );
            """);
        await EnsurePostgresColumn(
            db,
            "PlayerProfiles",
            "ZaloUserId",
            "\"ZaloUserId\" text NULL");

        await EnsurePostgresColumn(
            db,
            "PlayerProfiles",
            "DisplayName",
            "\"DisplayName\" text NOT NULL DEFAULT ''");

        await EnsurePostgresColumn(
            db,
            "PlayerProfiles",
            "AvatarUrl",
            "\"AvatarUrl\" text NULL");

        await EnsurePostgresColumn(
            db,
            "PlayerProfiles",
            "Gender",
            "\"Gender\" text NULL");

        await EnsurePostgresColumn(
            db,
            "PlayerProfiles",
            "DefaultRole",
            "\"DefaultRole\" text NULL");

        await EnsurePostgresColumn(
            db,
            "PlayerProfiles",
            "DefaultLevel",
            "\"DefaultLevel\" text NULL");

        await EnsurePostgresColumn(
            db,
            "PlayerProfiles",
            "LastSyncedAt",
            "\"LastSyncedAt\" timestamp with time zone NOT NULL DEFAULT CURRENT_TIMESTAMP");

        await EnsurePostgresColumn(
            db,
            "PlayerProfiles",
            "GenderUpdatedAt",
            "\"GenderUpdatedAt\" timestamp with time zone NULL");

        await EnsurePostgresColumn(
            db,
            "PlayerProfiles",
            "GenderUpdatedByUserId",
            "\"GenderUpdatedByUserId\" text NULL");

        await EnsurePostgresColumn(
            db,
            "PlayerProfiles",
            "CreatedAt",
            "\"CreatedAt\" timestamp with time zone NOT NULL DEFAULT CURRENT_TIMESTAMP");

        await EnsurePostgresColumn(
            db,
            "PlayerProfiles",
            "UpdatedAt",
            "\"UpdatedAt\" timestamp with time zone NOT NULL DEFAULT CURRENT_TIMESTAMP");

        await db.Database.ExecuteSqlRawAsync(
            """
            ALTER TABLE "PlayerProfiles"
            ALTER COLUMN "Gender" DROP NOT NULL;
            """
        );

        await db.Database.ExecuteSqlRawAsync(
            """
            ALTER TABLE "PlayerProfiles"
            DROP COLUMN IF EXISTS "AdminUserId" CASCADE;
            """
                );

        await db.Database.ExecuteSqlRawAsync(
            """
            ALTER TABLE "PlayerProfiles"
            DROP COLUMN IF EXISTS "Role";
            """
                );

        await db.Database.ExecuteSqlRawAsync(
            """
            ALTER TABLE "PlayerProfiles"
            DROP COLUMN IF EXISTS "Level";
            """
                );

        await db.Database.ExecuteSqlRawAsync(
            """CREATE UNIQUE INDEX IF NOT EXISTS "IX_PlayerProfiles_ZaloUserId" ON "PlayerProfiles" ("ZaloUserId");""");
        await db.Database.ExecuteSqlRawAsync(
            """CREATE UNIQUE INDEX IF NOT EXISTS "IX_ZaloConnections_AdminUserId_AccountZaloId" ON "ZaloConnections" ("AdminUserId", "AccountZaloId");""");
        await db.Database.ExecuteSqlRawAsync(
            """CREATE INDEX IF NOT EXISTS "IX_ZaloConnections_AdminUserId" ON "ZaloConnections" ("AdminUserId");""");
        await db.Database.ExecuteSqlRawAsync(
            """CREATE INDEX IF NOT EXISTS "IX_PollImports_SessionId_PollId" ON "PollImports" ("SessionId", "PollId");""");
        await db.Database.ExecuteSqlRawAsync(
            """CREATE UNIQUE INDEX IF NOT EXISTS "IX_SessionPlayers_SessionId_PlayerProfileId" ON "SessionPlayers" ("SessionId", "PlayerProfileId") WHERE "PlayerProfileId" IS NOT NULL;""");
        await db.Database.ExecuteSqlRawAsync(
            """CREATE INDEX IF NOT EXISTS "IX_MatchSessions_ZaloConnectionId" ON "MatchSessions" ("ZaloConnectionId");""");
    }

    private static async Task OpenIfNeeded(VolleyDraftDbContext db)
    {
        var connection = db.Database.GetDbConnection();
        if (connection.State != ConnectionState.Open)
        {
            await connection.OpenAsync();
        }
    }

    private static async Task EnsureLegacySqlitePlayerProfileColumnDropped(VolleyDraftDbContext db)
    {
        await using var command = db.Database.GetDbConnection().CreateCommand();
        command.CommandText = "SELECT COUNT(*) FROM pragma_table_info('PlayerProfiles') WHERE name = 'AdminUserId'";
        await OpenIfNeeded(db);
        var count = Convert.ToInt32(await command.ExecuteScalarAsync());
        if (count > 0)
        {
            await db.Database.ExecuteSqlRawAsync("ALTER TABLE \"PlayerProfiles\" DROP COLUMN \"AdminUserId\"");
        }
    }

    private static async Task EnsureSqliteZaloBotTables(VolleyDraftDbContext db)
    {
        await db.Database.ExecuteSqlRawAsync("""
            CREATE TABLE IF NOT EXISTS "ZaloGroupMessages" (
                "Id" TEXT NOT NULL CONSTRAINT "PK_ZaloGroupMessages" PRIMARY KEY,
                "ZaloConnectionId" TEXT NOT NULL,
                "GroupId" TEXT NOT NULL,
                "MessageId" TEXT NOT NULL,
                "SenderId" TEXT NOT NULL,
                "SenderName" TEXT NOT NULL,
                "Content" TEXT NOT NULL,
                "IsFromBot" INTEGER NOT NULL,
                "SentAt" TEXT NOT NULL,
                "ReceivedAt" TEXT NOT NULL,
                "ReplyAttemptCount" INTEGER NOT NULL DEFAULT 0,
                "BotReplySentAt" TEXT NULL,
                CONSTRAINT "FK_ZaloGroupMessages_ZaloConnections_ZaloConnectionId"
                    FOREIGN KEY ("ZaloConnectionId") REFERENCES "ZaloConnections" ("Id") ON DELETE CASCADE
            );
            """);
        await db.Database.ExecuteSqlRawAsync(
            """CREATE UNIQUE INDEX IF NOT EXISTS "IX_ZaloGroupMessages_ZaloConnectionId_MessageId" ON "ZaloGroupMessages" ("ZaloConnectionId", "MessageId");""");
        await db.Database.ExecuteSqlRawAsync(
            """CREATE INDEX IF NOT EXISTS "IX_ZaloGroupMessages_ZaloConnectionId_GroupId_SentAt" ON "ZaloGroupMessages" ("ZaloConnectionId", "GroupId", "SentAt");""");
    }

    private static async Task EnsurePostgresZaloBotTables(VolleyDraftDbContext db)
    {
        await db.Database.ExecuteSqlRawAsync("""
            CREATE TABLE IF NOT EXISTS "ZaloGroupMessages" (
                "Id" text NOT NULL CONSTRAINT "PK_ZaloGroupMessages" PRIMARY KEY,
                "ZaloConnectionId" text NOT NULL,
                "GroupId" text NOT NULL,
                "MessageId" text NOT NULL,
                "SenderId" text NOT NULL,
                "SenderName" text NOT NULL,
                "Content" text NOT NULL,
                "IsFromBot" boolean NOT NULL,
                "SentAt" timestamp with time zone NOT NULL,
                "ReceivedAt" timestamp with time zone NOT NULL,
                "ReplyAttemptCount" integer NOT NULL DEFAULT 0,
                "BotReplySentAt" timestamp with time zone NULL,
                CONSTRAINT "FK_ZaloGroupMessages_ZaloConnections_ZaloConnectionId"
                    FOREIGN KEY ("ZaloConnectionId") REFERENCES "ZaloConnections" ("Id") ON DELETE CASCADE
            );
            """);
        await db.Database.ExecuteSqlRawAsync(
            """CREATE UNIQUE INDEX IF NOT EXISTS "IX_ZaloGroupMessages_ZaloConnectionId_MessageId" ON "ZaloGroupMessages" ("ZaloConnectionId", "MessageId");""");
        await db.Database.ExecuteSqlRawAsync(
            """CREATE INDEX IF NOT EXISTS "IX_ZaloGroupMessages_ZaloConnectionId_GroupId_SentAt" ON "ZaloGroupMessages" ("ZaloConnectionId", "GroupId", "SentAt");""");
    }

    private static async Task EnsureSqliteZaloBotLearningTables(VolleyDraftDbContext db)
    {
        await db.Database.ExecuteSqlRawAsync("""
            CREATE TABLE IF NOT EXISTS "ZaloBotLearnedRules" (
                "Id" TEXT NOT NULL CONSTRAINT "PK_ZaloBotLearnedRules" PRIMARY KEY,
                "ZaloConnectionId" TEXT NOT NULL,
                "GroupId" TEXT NOT NULL,
                "Trigger" TEXT NOT NULL,
                "NormalizedTrigger" TEXT NOT NULL,
                "Answer" TEXT NOT NULL,
                "CreatedBySenderId" TEXT NOT NULL,
                "CreatedBySenderName" TEXT NOT NULL,
                "CreatedAt" TEXT NOT NULL,
                "UpdatedAt" TEXT NOT NULL,
                CONSTRAINT "FK_ZaloBotLearnedRules_ZaloConnections_ZaloConnectionId"
                    FOREIGN KEY ("ZaloConnectionId") REFERENCES "ZaloConnections" ("Id") ON DELETE CASCADE
            );
            """);
        await db.Database.ExecuteSqlRawAsync(
            """CREATE UNIQUE INDEX IF NOT EXISTS "IX_ZaloBotLearnedRules_Connection_Group_Trigger" ON "ZaloBotLearnedRules" ("ZaloConnectionId", "GroupId", "NormalizedTrigger");""");
    }

    private static async Task EnsurePostgresZaloBotLearningTables(VolleyDraftDbContext db)
    {
        await db.Database.ExecuteSqlRawAsync("""
            CREATE TABLE IF NOT EXISTS "ZaloBotLearnedRules" (
                "Id" text NOT NULL CONSTRAINT "PK_ZaloBotLearnedRules" PRIMARY KEY,
                "ZaloConnectionId" text NOT NULL,
                "GroupId" text NOT NULL,
                "Trigger" text NOT NULL,
                "NormalizedTrigger" text NOT NULL,
                "Answer" text NOT NULL,
                "CreatedBySenderId" text NOT NULL,
                "CreatedBySenderName" text NOT NULL,
                "CreatedAt" timestamp with time zone NOT NULL,
                "UpdatedAt" timestamp with time zone NOT NULL,
                CONSTRAINT "FK_ZaloBotLearnedRules_ZaloConnections_ZaloConnectionId"
                    FOREIGN KEY ("ZaloConnectionId") REFERENCES "ZaloConnections" ("Id") ON DELETE CASCADE
            );
            """);
        await db.Database.ExecuteSqlRawAsync(
            """CREATE UNIQUE INDEX IF NOT EXISTS "IX_ZaloBotLearnedRules_Connection_Group_Trigger" ON "ZaloBotLearnedRules" ("ZaloConnectionId", "GroupId", "NormalizedTrigger");""");
    }

    private static async Task EnsureSqliteZaloBotImageTables(VolleyDraftDbContext db)
    {
        await db.Database.ExecuteSqlRawAsync("""
            CREATE TABLE IF NOT EXISTS "ZaloBotImageAssets" (
                "Id" TEXT NOT NULL CONSTRAINT "PK_ZaloBotImageAssets" PRIMARY KEY,
                "AdminUserId" TEXT NOT NULL,
                "FileName" TEXT NOT NULL,
                "ContentType" TEXT NOT NULL,
                "Size" INTEGER NOT NULL,
                "Data" BLOB NOT NULL,
                "CreatedAt" TEXT NOT NULL,
                CONSTRAINT "FK_ZaloBotImageAssets_Users_AdminUserId"
                    FOREIGN KEY ("AdminUserId") REFERENCES "Users" ("Id") ON DELETE CASCADE
            );
            """);
        await db.Database.ExecuteSqlRawAsync(
            """CREATE INDEX IF NOT EXISTS "IX_ZaloBotImageAssets_AdminUserId_CreatedAt" ON "ZaloBotImageAssets" ("AdminUserId", "CreatedAt");""");
    }

    private static async Task EnsurePostgresZaloBotImageTables(VolleyDraftDbContext db)
    {
        await db.Database.ExecuteSqlRawAsync("""
            CREATE TABLE IF NOT EXISTS "ZaloBotImageAssets" (
                "Id" text NOT NULL CONSTRAINT "PK_ZaloBotImageAssets" PRIMARY KEY,
                "AdminUserId" text NOT NULL,
                "FileName" text NOT NULL,
                "ContentType" text NOT NULL,
                "Size" bigint NOT NULL,
                "Data" bytea NOT NULL,
                "CreatedAt" timestamp with time zone NOT NULL,
                CONSTRAINT "FK_ZaloBotImageAssets_Users_AdminUserId"
                    FOREIGN KEY ("AdminUserId") REFERENCES "Users" ("Id") ON DELETE CASCADE
            );
            """);
        await db.Database.ExecuteSqlRawAsync(
            """CREATE INDEX IF NOT EXISTS "IX_ZaloBotImageAssets_AdminUserId_CreatedAt" ON "ZaloBotImageAssets" ("AdminUserId", "CreatedAt");""");
    }
}
