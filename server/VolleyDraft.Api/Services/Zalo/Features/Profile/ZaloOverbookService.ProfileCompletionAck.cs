namespace VolleyDraft.Api.Services;

public sealed partial class ZaloOverbookService
{
    private async Task<string> BuildSelfProfileCompletionReplyAsync(
        MatchSession session,
        ZaloMissingProfilePromptContext prompt,
        IReadOnlyList<string> accepted,
        bool alreadyComplete,
        CancellationToken cancellationToken)
    {
        var readiness = await new ZaloDraftReadinessService(db)
            .BuildAsync(session.Id, cancellationToken: cancellationToken);
        var readinessCopy = ZaloProfileUpdateReadinessCopy.Build(readiness);

        if (alreadyComplete)
        {
            return $"Hồ sơ {prompt.DisplayName} vừa đủ dữ liệu rồi 👌 Tui không ghi đè thêm.{readinessCopy}";
        }

        var acceptedCopy = accepted.Count > 0
            ? $" tui ghi {string.Join(" · ", accepted)} rồi."
            : string.Empty;
        return $"Ok {prompt.DisplayName} 😎{acceptedCopy} Hồ sơ kèo {session.Name} xong.{readinessCopy}";
    }
}
