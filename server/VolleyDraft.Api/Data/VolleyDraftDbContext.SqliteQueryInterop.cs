using System.Linq.Expressions;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;

namespace VolleyDraft.Api.Data;

public sealed partial class VolleyDraftDbContext
{
    private static readonly IInterceptor SqliteUtcNowInterceptor = new SqliteDateTimeOffsetUtcNowInterceptor();

    protected override void OnConfiguring(DbContextOptionsBuilder optionsBuilder)
    {
        base.OnConfiguring(optionsBuilder);
        optionsBuilder.AddInterceptors(SqliteUtcNowInterceptor);
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
