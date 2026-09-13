using VolleyDraft.Api.Data;
using Xunit;

namespace VolleyDraft.Api.Tests.Data;

public sealed class PostgresSchedulerSqlInteropTests
{
    [Fact]
    public void Scheduler_column_probe_uses_information_schema_on_postgres()
    {
        var sql = VolleyDraftDbContext.RewritePostgresSchedulerSql(
            """
            SELECT COUNT(*) AS "Value"
            FROM pragma_table_info('ZaloSchedulerLeases')
            WHERE "name" = 'AuthorityLeaseUntil'
            """);

        Assert.Contains("information_schema.columns", sql, StringComparison.Ordinal);
        Assert.Contains("AuthorityLeaseUntil", sql, StringComparison.Ordinal);
        Assert.DoesNotContain("pragma_table_info", sql, StringComparison.Ordinal);
    }

    [Fact]
    public void Scheduler_legacy_duration_migration_uses_postgres_timestamp_math()
    {
        var sql = VolleyDraftDbContext.RewritePostgresSchedulerSql(
            """
            UPDATE "ZaloSchedulerLeases"
            SET "LegacyLeaseDurationSeconds" = CASE
                WHEN "LastAttemptAt" IS NULL
                  OR julianday("LastAttemptAt") IS NULL
                  OR julianday("LeaseUntil") IS NULL
                    THEN 3600.0
                ELSE (julianday("LeaseUntil") - julianday("LastAttemptAt")) * 86400.0
            END
            WHERE "LegacyLeaseDurationSeconds" IS NULL;
            """);

        Assert.Contains("EXTRACT(EPOCH", sql, StringComparison.Ordinal);
        Assert.Contains("::timestamptz", sql, StringComparison.Ordinal);
        Assert.DoesNotContain("julianday", sql, StringComparison.Ordinal);
    }

    [Fact]
    public void Scheduler_authority_migration_and_clock_use_postgres_clock()
    {
        var migrationSql = VolleyDraftDbContext.RewritePostgresSchedulerSql(
            """
            UPDATE "ZaloSchedulerLeases"
            SET "AuthorityLeaseUntil" = CASE
                WHEN "OwnerId" LIKE 'released:%'
                    THEN strftime('%Y-%m-%dT%H:%M:%f0000+00:00', 'now')
                ELSE strftime(
                    '%Y-%m-%dT%H:%M:%f0000+00:00',
                    'now',
                    printf('+%f seconds', COALESCE("LegacyLeaseDurationSeconds", 3600.0)))
            END
            WHERE "AuthorityLeaseUntil" IS NULL;
            """);
        var clockSql = VolleyDraftDbContext.RewritePostgresSchedulerSql(
            "SELECT strftime('%Y-%m-%dT%H:%M:%f0000+00:00', 'now') AS \"Value\"");

        Assert.Contains("clock_timestamp()", migrationSql, StringComparison.Ordinal);
        Assert.Contains("interval '1 second'", migrationSql, StringComparison.Ordinal);
        Assert.DoesNotContain("strftime", migrationSql, StringComparison.Ordinal);
        Assert.DoesNotContain("printf", migrationSql, StringComparison.Ordinal);
        Assert.Contains("clock_timestamp()", clockSql, StringComparison.Ordinal);
        Assert.DoesNotContain("strftime", clockSql, StringComparison.Ordinal);
    }

    [Fact]
    public void Scheduler_sqlite_trigger_metadata_and_ddl_do_not_reach_npgsql()
    {
        var metadataSql = VolleyDraftDbContext.RewritePostgresSchedulerSql(
            """
            SELECT COUNT(*) AS "Value"
            FROM sqlite_master
            WHERE type = 'trigger'
              AND name = 'ZaloSchedulerLegacyTakeoverFence'
            """);
        var triggerSql = VolleyDraftDbContext.RewritePostgresSchedulerSql(
            """
            CREATE TRIGGER IF NOT EXISTS "ZaloSchedulerLegacyTakeoverFence"
            BEFORE UPDATE OF "OwnerId", "LeaseUntil" ON "ZaloSchedulerLeases"
            BEGIN
                SELECT RAISE(IGNORE);
            END;
            """);

        Assert.DoesNotContain("sqlite_master", metadataSql, StringComparison.Ordinal);
        Assert.DoesNotContain("CREATE TRIGGER", triggerSql, StringComparison.Ordinal);
        Assert.DoesNotContain("RAISE", triggerSql, StringComparison.Ordinal);
    }

    [Fact]
    public void Non_scheduler_sql_is_not_rewritten()
    {
        const string sql = "SELECT \"Id\" FROM \"ZaloConnections\";";

        Assert.Equal(sql, VolleyDraftDbContext.RewritePostgresSchedulerSql(sql));
    }
}
