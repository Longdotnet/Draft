using Microsoft.EntityFrameworkCore;

namespace VolleyDraft.Api.Data;

public static partial class DatabaseSchemaPatch
{
    private static async Task EnsureSqliteScheduledDraftAndMembershipTables(VolleyDraftDbContext db)
    {
        await db.Database.ExecuteSqlRawAsync("""
            CREATE TABLE IF NOT EXISTS "ZaloGroupMembershipPeriods" (
                "Id" TEXT NOT NULL CONSTRAINT "PK_ZaloGroupMembershipPeriods" PRIMARY KEY,
                "ZaloConnectionId" TEXT NOT NULL,
                "GroupId" TEXT NOT NULL,
                "ZaloUserId" TEXT NOT NULL,
                "DisplayNameSnapshot" TEXT NOT NULL,
                "JoinedAt" TEXT NULL,
                "JoinEvidenceSource" TEXT NOT NULL,
                "JoinEventId" TEXT NULL,
                "LeftAt" TEXT NULL,
                "LeaveEvidenceSource" TEXT NULL,
                "LeaveEventId" TEXT NULL,
                "FirstObservedAt" TEXT NOT NULL,
                "LastObservedAt" TEXT NOT NULL,
                "IsCurrentPeriod" INTEGER NOT NULL,
                "CreatedAt" TEXT NOT NULL,
                "UpdatedAt" TEXT NOT NULL
            );
            CREATE INDEX IF NOT EXISTS "IX_ZaloGroupMembershipPeriods_Current"
                ON "ZaloGroupMembershipPeriods" ("ZaloConnectionId", "GroupId", "ZaloUserId", "IsCurrentPeriod");
            CREATE INDEX IF NOT EXISTS "IX_ZaloGroupMembershipPeriods_Joined"
                ON "ZaloGroupMembershipPeriods" ("ZaloConnectionId", "GroupId", "JoinedAt");
            CREATE UNIQUE INDEX IF NOT EXISTS "IX_ZaloGroupMembershipPeriods_JoinEventId"
                ON "ZaloGroupMembershipPeriods" ("JoinEventId");
            CREATE UNIQUE INDEX IF NOT EXISTS "IX_ZaloGroupMembershipPeriods_LeaveEventId"
                ON "ZaloGroupMembershipPeriods" ("LeaveEventId");

            CREATE TABLE IF NOT EXISTS "ZaloMembershipCoverages" (
                "Id" TEXT NOT NULL CONSTRAINT "PK_ZaloMembershipCoverages" PRIMARY KEY,
                "ZaloConnectionId" TEXT NOT NULL,
                "GroupId" TEXT NOT NULL,
                "ProviderEventTrackingStartedAt" TEXT NULL,
                "LastProviderEventAt" TEXT NULL,
                "LastDirectorySyncAt" TEXT NULL,
                "LastDirectorySyncWasComplete" INTEGER NOT NULL,
                "HasKnownGap" INTEGER NOT NULL,
                "CreatedAt" TEXT NOT NULL,
                "UpdatedAt" TEXT NOT NULL
            );
            CREATE UNIQUE INDEX IF NOT EXISTS "IX_ZaloMembershipCoverages_Scope"
                ON "ZaloMembershipCoverages" ("ZaloConnectionId", "GroupId");

            CREATE TABLE IF NOT EXISTS "ZaloScheduledDraftPolicies" (
                "Id" TEXT NOT NULL CONSTRAINT "PK_ZaloScheduledDraftPolicies" PRIMARY KEY,
                "ZaloConnectionId" TEXT NOT NULL,
                "GroupId" TEXT NOT NULL,
                "Enabled" INTEGER NOT NULL,
                "LocalDraftMinuteOfDay" INTEGER NOT NULL,
                "ReminderLeadMinutes" INTEGER NOT NULL,
                "TimeZoneId" TEXT NOT NULL,
                "EnabledByZaloUserId" TEXT NULL,
                "EnabledAt" TEXT NULL,
                "Version" INTEGER NOT NULL,
                "CreatedAt" TEXT NOT NULL,
                "UpdatedAt" TEXT NOT NULL
            );
            CREATE UNIQUE INDEX IF NOT EXISTS "IX_ZaloScheduledDraftPolicies_Scope"
                ON "ZaloScheduledDraftPolicies" ("ZaloConnectionId", "GroupId");

            CREATE TABLE IF NOT EXISTS "ZaloScheduledDraftRuns" (
                "Id" TEXT NOT NULL CONSTRAINT "PK_ZaloScheduledDraftRuns" PRIMARY KEY,
                "ZaloConnectionId" TEXT NOT NULL,
                "GroupId" TEXT NOT NULL,
                "SessionId" TEXT NOT NULL,
                "PolicyVersion" INTEGER NOT NULL,
                "ReminderDueAt" TEXT NOT NULL,
                "ScheduledFor" TEXT NOT NULL,
                "State" TEXT NOT NULL,
                "OverrideKind" TEXT NOT NULL,
                "DeferredUntil" TEXT NULL,
                "OverrideByZaloUserId" TEXT NULL,
                "ReminderAttemptedAt" TEXT NULL,
                "ReminderSentAt" TEXT NULL,
                "ReminderMessageId" TEXT NULL,
                "RosterFingerprint" TEXT NULL,
                "LeaseToken" TEXT NULL,
                "LeaseUntil" TEXT NULL,
                "DraftStartedAt" TEXT NULL,
                "DraftCompletedAt" TEXT NULL,
                "LastError" TEXT NULL,
                "CreatedAt" TEXT NOT NULL,
                "UpdatedAt" TEXT NOT NULL
            );
            CREATE UNIQUE INDEX IF NOT EXISTS "IX_ZaloScheduledDraftRuns_Scope"
                ON "ZaloScheduledDraftRuns" ("ZaloConnectionId", "GroupId", "SessionId");
            CREATE INDEX IF NOT EXISTS "IX_ZaloScheduledDraftRuns_Due"
                ON "ZaloScheduledDraftRuns" ("State", "ScheduledFor");
            """);
    }

    private static async Task EnsurePostgresScheduledDraftAndMembershipTables(VolleyDraftDbContext db)
    {
        await db.Database.ExecuteSqlRawAsync("""
            CREATE TABLE IF NOT EXISTS "ZaloGroupMembershipPeriods" (
                "Id" text NOT NULL CONSTRAINT "PK_ZaloGroupMembershipPeriods" PRIMARY KEY,
                "ZaloConnectionId" text NOT NULL,
                "GroupId" text NOT NULL,
                "ZaloUserId" text NOT NULL,
                "DisplayNameSnapshot" text NOT NULL,
                "JoinedAt" timestamp with time zone NULL,
                "JoinEvidenceSource" text NOT NULL,
                "JoinEventId" text NULL,
                "LeftAt" timestamp with time zone NULL,
                "LeaveEvidenceSource" text NULL,
                "LeaveEventId" text NULL,
                "FirstObservedAt" timestamp with time zone NOT NULL,
                "LastObservedAt" timestamp with time zone NOT NULL,
                "IsCurrentPeriod" boolean NOT NULL,
                "CreatedAt" timestamp with time zone NOT NULL,
                "UpdatedAt" timestamp with time zone NOT NULL
            );
            CREATE INDEX IF NOT EXISTS "IX_ZaloGroupMembershipPeriods_Current"
                ON "ZaloGroupMembershipPeriods" ("ZaloConnectionId", "GroupId", "ZaloUserId", "IsCurrentPeriod");
            CREATE INDEX IF NOT EXISTS "IX_ZaloGroupMembershipPeriods_Joined"
                ON "ZaloGroupMembershipPeriods" ("ZaloConnectionId", "GroupId", "JoinedAt");
            CREATE UNIQUE INDEX IF NOT EXISTS "IX_ZaloGroupMembershipPeriods_JoinEventId"
                ON "ZaloGroupMembershipPeriods" ("JoinEventId") WHERE "JoinEventId" IS NOT NULL;
            CREATE UNIQUE INDEX IF NOT EXISTS "IX_ZaloGroupMembershipPeriods_LeaveEventId"
                ON "ZaloGroupMembershipPeriods" ("LeaveEventId") WHERE "LeaveEventId" IS NOT NULL;

            CREATE TABLE IF NOT EXISTS "ZaloMembershipCoverages" (
                "Id" text NOT NULL CONSTRAINT "PK_ZaloMembershipCoverages" PRIMARY KEY,
                "ZaloConnectionId" text NOT NULL,
                "GroupId" text NOT NULL,
                "ProviderEventTrackingStartedAt" timestamp with time zone NULL,
                "LastProviderEventAt" timestamp with time zone NULL,
                "LastDirectorySyncAt" timestamp with time zone NULL,
                "LastDirectorySyncWasComplete" boolean NOT NULL,
                "HasKnownGap" boolean NOT NULL,
                "CreatedAt" timestamp with time zone NOT NULL,
                "UpdatedAt" timestamp with time zone NOT NULL
            );
            CREATE UNIQUE INDEX IF NOT EXISTS "IX_ZaloMembershipCoverages_Scope"
                ON "ZaloMembershipCoverages" ("ZaloConnectionId", "GroupId");

            CREATE TABLE IF NOT EXISTS "ZaloScheduledDraftPolicies" (
                "Id" text NOT NULL CONSTRAINT "PK_ZaloScheduledDraftPolicies" PRIMARY KEY,
                "ZaloConnectionId" text NOT NULL,
                "GroupId" text NOT NULL,
                "Enabled" boolean NOT NULL,
                "LocalDraftMinuteOfDay" integer NOT NULL,
                "ReminderLeadMinutes" integer NOT NULL,
                "TimeZoneId" text NOT NULL,
                "EnabledByZaloUserId" text NULL,
                "EnabledAt" timestamp with time zone NULL,
                "Version" bigint NOT NULL,
                "CreatedAt" timestamp with time zone NOT NULL,
                "UpdatedAt" timestamp with time zone NOT NULL
            );
            CREATE UNIQUE INDEX IF NOT EXISTS "IX_ZaloScheduledDraftPolicies_Scope"
                ON "ZaloScheduledDraftPolicies" ("ZaloConnectionId", "GroupId");

            CREATE TABLE IF NOT EXISTS "ZaloScheduledDraftRuns" (
                "Id" text NOT NULL CONSTRAINT "PK_ZaloScheduledDraftRuns" PRIMARY KEY,
                "ZaloConnectionId" text NOT NULL,
                "GroupId" text NOT NULL,
                "SessionId" text NOT NULL,
                "PolicyVersion" bigint NOT NULL,
                "ReminderDueAt" timestamp with time zone NOT NULL,
                "ScheduledFor" timestamp with time zone NOT NULL,
                "State" text NOT NULL,
                "OverrideKind" text NOT NULL,
                "DeferredUntil" timestamp with time zone NULL,
                "OverrideByZaloUserId" text NULL,
                "ReminderAttemptedAt" timestamp with time zone NULL,
                "ReminderSentAt" timestamp with time zone NULL,
                "ReminderMessageId" text NULL,
                "RosterFingerprint" text NULL,
                "LeaseToken" text NULL,
                "LeaseUntil" timestamp with time zone NULL,
                "DraftStartedAt" timestamp with time zone NULL,
                "DraftCompletedAt" timestamp with time zone NULL,
                "LastError" text NULL,
                "CreatedAt" timestamp with time zone NOT NULL,
                "UpdatedAt" timestamp with time zone NOT NULL
            );
            CREATE UNIQUE INDEX IF NOT EXISTS "IX_ZaloScheduledDraftRuns_Scope"
                ON "ZaloScheduledDraftRuns" ("ZaloConnectionId", "GroupId", "SessionId");
            CREATE INDEX IF NOT EXISTS "IX_ZaloScheduledDraftRuns_Due"
                ON "ZaloScheduledDraftRuns" ("State", "ScheduledFor");
            """);
    }
}
