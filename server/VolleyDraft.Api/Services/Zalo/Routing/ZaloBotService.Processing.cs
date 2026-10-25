using Microsoft.EntityFrameworkCore;

namespace VolleyDraft.Api.Services;

public sealed partial class ZaloBotService
{
    private async Task FinishMessageAsync(
        string id,
        string token,
        ZaloBotIntent? intent,
        bool aiCalled,
        string outcome,
        CancellationToken cancellationToken)
    {
        await db.ZaloGroupMessages.Where(message => message.Id == id && message.ProcessingToken == token)
            .ExecuteUpdateAsync(updates => updates
                .SetProperty(message => message.BotReplySentAt, outcome == "sent" ? DateTimeOffset.UtcNow : null)
                .SetProperty(message => message.SelectedIntent, intent == null ? null : intent.Value.ToString())
                .SetProperty(message => message.AiCalled, aiCalled)
                .SetProperty(message => message.ReplyOutcome, outcome), cancellationToken);
    }

    private async Task ReleaseMessageAsync(
        string id,
        string token,
        string outcome,
        CancellationToken cancellationToken)
    {
        await db.ZaloGroupMessages.Where(message => message.Id == id && message.ProcessingToken == token)
            .ExecuteUpdateAsync(updates => updates
                .SetProperty(message => message.ProcessingStartedAt, (DateTimeOffset?)null)
                .SetProperty(message => message.ProcessingToken, (string?)null)
                .SetProperty(message => message.ReplyOutcome, outcome), cancellationToken);
    }
}
