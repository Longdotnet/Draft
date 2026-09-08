using Microsoft.EntityFrameworkCore;

namespace VolleyDraft.Api.Services;

public sealed partial class ZaloOverbookService
{
    private async Task SendProfileCompletionReadinessFollowUpsAsync(
        IReadOnlyList<ZaloMissingProfilePromptContext> activeAtCycleStart,
        DateTimeOffset cycleStartedAt,
        CancellationToken cancellationToken)
    {
        foreach (var prompt in activeAtCycleStart)
        {
            var completedByThisCycle = await db.ZaloGroupMessages
                .AsNoTracking()
                .AnyAsync(message =>
                    message.ZaloConnectionId == prompt.ZaloConnectionId &&
                    message.GroupId == prompt.GroupId &&
                    message.SenderId == prompt.ZaloUserId &&
                    message.BotReplySentAt != null &&
                    message.BotReplySentAt >= cycleStartedAt &&
                    (message.ReplyOutcome == "profile_updated" ||
                     message.ReplyOutcome == "profile_semantic_updated"),
                    cancellationToken);
            if (!completedByThisCycle) continue;

            var player = await LoadProfilePromptPlayerAsync(prompt, cancellationToken);
            if (player is null) continue;
            var missing = GetMissingProfileFlags(player);
            if (missing.Gender || missing.Role || missing.Level) continue;

            var session = await db.MatchSessions
                .AsNoTracking()
                .Include(item => item.ZaloConnection)
                .SingleOrDefaultAsync(item =>
                    item.Id == prompt.SessionId &&
                    item.ZaloConnectionId == prompt.ZaloConnectionId &&
                    item.ZaloGroupId == prompt.GroupId,
                    cancellationToken);
            if (session?.ZaloConnection is null || string.IsNullOrWhiteSpace(session.ZaloGroupId))
                continue;

            var readiness = await new ZaloDraftReadinessService(db)
                .BuildAsync(session.Id, cancellationToken: cancellationToken);
            var text = $"{prompt.DisplayName} đã bổ sung đủ hồ sơ.{ZaloProfileUpdateReadinessCopy.Build(readiness)}";
            var send = await bridge.SendGroupMessageAsync(
                session.ZaloConnection.AccountZaloId,
                session.ZaloGroupId,
                text,
                [],
                idempotencyKey: $"profile-readiness:{prompt.Id}");
            if (!send.Sent)
                continue;

            var providerMessageId = NormalizeProviderMessageId(send.MessageId);
            if (providerMessageId is not null)
                await SaveBotMessageAsync(session, providerMessageId, text, DateTimeOffset.UtcNow, cancellationToken);
        }
    }
}
