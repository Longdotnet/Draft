using System.Data.Common;
using System.Linq.Expressions;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.EntityFrameworkCore.Storage.ValueConversion;

namespace VolleyDraft.Api.Data;

public sealed partial class VolleyDraftDbContext
{
    private static readonly IInterceptor SqliteUtcNowInterceptor = new SqliteDateTimeOffsetUtcNowInterceptor();
    private static readonly IInterceptor PostgresSchedulerSqlInterceptor = new PostgresSchedulerSqlCommandInterceptor();

    protected override void ConfigureConventions(ModelConfigurationBuilder configurationBuilder)
    {
        base.ConfigureConventions(configurationBuilder);
        if (Database.IsSqlite())
        {
            configurationBuilder
                .Properties<DateTimeOffset>()
                .HaveConversion<UtcDateTimeOffsetConverter>();
        }
    }

    protected override void OnConfiguring(DbContextOptionsBuilder optionsBuilder)
    {
        base.OnConfiguring(optionsBuilder);
        optionsBuilder.AddInterceptors(SqliteUtcNowInterceptor, PostgresSchedulerSqlInterceptor);
    }

    /// <summary>
    /// Scheduler lease compatibility migrations were introduced using SQLite primitives because the
    /// fuzz harness executes against SQLite. Production Render deployments use PostgreSQL, so keep
    /// the scheduler's raw-SQL contract provider-safe at the DbContext boundary until the lease
    /// schema is moved into normal provider-specific migrations.
    /// </summary>
    internal static string RewritePostgresSchedulerSql(string commandText)
    {
        if (string.IsNullOrWhiteSpace(commandText))
            return commandText;

        if (commandText.Contains("pragma_table_info('ZaloSchedulerLeases')", StringComparison.Ordinal))
        {
            var columnName = commandText.Contains("'AuthorityLeaseUntil'", StringComparison.Ordinal)
                ? "AuthorityLeaseUntil"
                : commandText.Contains("'LegacyLeaseDurationSeconds'", StringComparison.Ordinal)
                    ? "LegacyLeaseDurationSeconds"
                    : null;

            if (columnName is not null)
            {
                return $$"""
                    SELECT COUNT(*)::int AS "Value"
                    FROM information_schema.columns
                    WHERE table_schema = current_schema()
                      AND table_name = 'ZaloSchedulerLeases'
                      AND column_name = '{{columnName}}'
                    """;
            }
        }

        if (commandText.Contains("julianday(\"LastAttemptAt\")", StringComparison.Ordinal))
        {
            return """
                UPDATE "ZaloSchedulerLeases"
                SET "LegacyLeaseDurationSeconds" = CASE
                    WHEN "LastAttemptAt" IS NULL
                      OR NULLIF("LastAttemptAt", '') IS NULL
                      OR NULLIF("LeaseUntil", '') IS NULL
                        THEN 3600.0
                    ELSE LEAST(
                        3600.0,
                        GREATEST(
                            120.0,
                            EXTRACT(EPOCH FROM (
                                "LeaseUntil"::timestamptz - "LastAttemptAt"::timestamptz))))
                END
                WHERE "LegacyLeaseDurationSeconds" IS NULL;
                """;
        }

        if (commandText.Contains("SET \"AuthorityLeaseUntil\" = CASE", StringComparison.Ordinal)
            && commandText.Contains("strftime(", StringComparison.Ordinal))
        {
            return """
                UPDATE "ZaloSchedulerLeases"
                SET "AuthorityLeaseUntil" = CASE
                    WHEN "OwnerId" LIKE 'released:%'
                        THEN to_char(
                            clock_timestamp() AT TIME ZONE 'UTC',
                            'YYYY-MM-DD"T"HH24:MI:SS.US') || '0+00:00'
                    ELSE to_char(
                        (clock_timestamp() +
                            (COALESCE("LegacyLeaseDurationSeconds", 3600.0) * interval '1 second'))
                            AT TIME ZONE 'UTC',
                        'YYYY-MM-DD"T"HH24:MI:SS.US') || '0+00:00'
                END
                WHERE "AuthorityLeaseUntil" IS NULL;
                """;
        }

        if (commandText.Contains("FROM sqlite_master", StringComparison.Ordinal)
            && commandText.Contains("ZaloSchedulerLegacy", StringComparison.Ordinal))
        {
            // The sqlite_master probes only version SQLite compatibility triggers. PostgreSQL never
            // had those SQLite trigger definitions, so there is no stale definition to replace.
            return "SELECT 0::int AS \"Value\";";
        }

        if (commandText.Contains("CREATE TRIGGER IF NOT EXISTS \"ZaloSchedulerLegacyTakeoverFence\"", StringComparison.Ordinal)
            || commandText.Contains("CREATE TRIGGER IF NOT EXISTS \"ZaloSchedulerLegacyExpiredStatusFence\"", StringComparison.Ordinal))
        {
            // These triggers bridge pre-authority SQLite binaries during a rolling upgrade. They use
            // SQLite-only WHEN/RAISE/strftime syntax and are not part of the current-version lease
            // authority algorithm. Current PostgreSQL workers still use the shared DB clock and the
            // AuthorityLeaseUntil fence in every acquire/renew/terminal mutation.
            return "SELECT 1;";
        }

        if (commandText.Contains(
                "SELECT strftime('%Y-%m-%dT%H:%M:%f0000+00:00', 'now') AS \"Value\"",
                StringComparison.Ordinal))
        {
            return """
                SELECT to_char(
                    clock_timestamp() AT TIME ZONE 'UTC',
                    'YYYY-MM-DD"T"HH24:MI:SS.US') || '0+00:00' AS "Value"
                """;
        }

        return commandText;
    }

    /// <summary>
    /// SQLite does not natively support ordering/comparison over DateTimeOffset.
    /// Normalize SQLite provider values to UTC DateTime while keeping DateTimeOffset
    /// in the domain model. Explicit property mappings in OnModelCreating can still
    /// override this pre-convention where a nullable/custom conversion is required.
    /// Non-SQLite providers keep their original mappings.
    /// </summary>
    private sealed class UtcDateTimeOffsetConverter : ValueConverter<DateTimeOffset, DateTime>
    {
        public UtcDateTimeOffsetConverter()
            : base(
                value => value.UtcDateTime,
                value => new DateTimeOffset(DateTime.SpecifyKind(value, DateTimeKind.Utc)))
        {
        }
    }

    /// <summary>
    /// SQLite can use the model's UTC DateTime provider conversions for
    /// DateTimeOffset-backed metadata, but ExecuteUpdate still cannot translate a
    /// value lambda that directly references DateTimeOffset.UtcNow. Evaluate only
    /// that static member into a query-compilation-time constant for SQLite so the
    /// normal property value converter can translate the resulting constant.
    ///
    /// PostgreSQL and every non-SQLite provider keep their original expression tree.
    /// </summary>
    private sealed class SqliteDateTimeOffsetUtcNowInterceptor : IQueryExpressionInterceptor
    {
        public Expression QueryCompilationStarting(
            Expression queryExpression,
            QueryExpressionEventData eventData)
        {
            if (eventData.Context?.Database.IsSqlite() != true)
                return queryExpression;

            return new UtcNowVisitor().Visit(queryExpression);
        }

        private sealed class UtcNowVisitor : ExpressionVisitor
        {
            protected override Expression VisitMember(MemberExpression node)
            {
                if (node.Expression is null &&
                    node.Member.DeclaringType == typeof(DateTimeOffset) &&
                    string.Equals(node.Member.Name, nameof(DateTimeOffset.UtcNow), StringComparison.Ordinal))
                {
                    return Expression.Constant(DateTimeOffset.UtcNow, typeof(DateTimeOffset));
                }

                return base.VisitMember(node);
            }
        }
    }

    /// <summary>
    /// Rewrites only the scheduler's known SQLite compatibility statements when the command is
    /// about to execute through Npgsql. Every unrelated command and every SQLite command is left
    /// untouched.
    /// </summary>
    private sealed class PostgresSchedulerSqlCommandInterceptor : DbCommandInterceptor
    {
        public override DbCommand CommandInitialized(CommandEndEventData eventData, DbCommand result)
        {
            if (eventData.Context?.Database.ProviderName?.Contains("Npgsql", StringComparison.OrdinalIgnoreCase) == true)
                result.CommandText = RewritePostgresSchedulerSql(result.CommandText);

            return result;
        }
    }
}
