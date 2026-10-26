using VolleyDraft.Api.Contracts;
using VolleyDraft.Api.Data;

namespace VolleyDraft.Api.Services;

internal static class ZaloDirectedAudienceBotIntegration
{
    public static Task<ZaloDirectedAudienceReply?> TryHandleAsync(
        VolleyDraftDbContext db,
        ZaloIntegrationService integration,
        IConfiguration configuration,
        ILogger logger,
        string connectionId,
        string groupId,
        ZaloIncomingMessageEvent incoming,
        string instruction,
        CancellationToken cancellationToken) =>
        new ZaloDirectedAudienceMessageService(db, integration, configuration, logger)
            .TryHandleAsync(connectionId, groupId, incoming, instruction, cancellationToken);

    public static async Task RememberAsync(
        VolleyDraftDbContext db,
        ILogger logger,
        string connectionId,
        string groupId,
        string requesterZaloUserId,
        string sourceMessageId,
        ZaloAudienceReferenceDraft? audience,
        CancellationToken cancellationToken)
    {
        if (audience is null) return;
        try
        {
            await new ZaloAudienceReferenceStore(db).RememberAsync(
                connectionId,
                groupId,
                requesterZaloUserId,
                sourceMessageId,
                audience,
                cancellationToken);
        }
        catch (Exception exception)
        {
            logger.LogWarning(
                exception,
                "Could not persist referenced Zalo audience Connection={ConnectionId} Group={GroupId} Message={MessageId}",
                connectionId,
                groupId,
                sourceMessageId);
        }
    }
}
