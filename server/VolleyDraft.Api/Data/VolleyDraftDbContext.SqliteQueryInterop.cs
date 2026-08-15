using System.Linq.Expressions;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.EntityFrameworkCore.Storage.ValueConversion;

namespace VolleyDraft.Api.Data;

public sealed partial class VolleyDraftDbContext
{
    private static readonly IInterceptor SqliteUtcNowInterceptor = new SqliteDateTimeOffsetUtcNowInterceptor();

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
        optionsBuilder.AddInterceptors(SqliteUtcNowInterceptor);
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
}
