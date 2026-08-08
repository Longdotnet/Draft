from pathlib import Path

path = Path("server/VolleyDraft.Api/Services/ZaloBotService.cs")
source = path.read_text(encoding="utf-8")

if "internal static bool HasCurrentPollVote(" in source:
    print("share self-service patch already applied")
    raise SystemExit(0)

old_sync = """                synced = await zaloIntegration.SyncLatestPollAsync(
                    session.AdminUserId,
                    session.Id,
                    session.Name);"""
new_sync = """                synced = await zaloIntegration.SyncLatestPollAsync(
                    session.AdminUserId,
                    session.Id);"""
if old_sync not in source:
    raise SystemExit("share poll sync call not found")
source = source.replace(old_sync, new_sync, 1)

marker = """        return refreshed is null
            ? new ShareSessionRefreshResult(null, \"Không tải lại được dữ liệu buổi sau khi kiểm tra poll. Bạn thử lại giúp mình nhé.\")
            : new ShareSessionRefreshResult(refreshed, null);
    }

    private async Task<BotAnswer> UpdatePlayerProfileAsync("""
helper = """        return refreshed is null
            ? new ShareSessionRefreshResult(null, \"Không tải lại được dữ liệu buổi sau khi kiểm tra poll. Bạn thử lại giúp mình nhé.\")
            : new ShareSessionRefreshResult(refreshed, null);
    }

    private async Task<bool> IsSenderCurrentPollVoterAsync(
        SessionSnapshot session,
        string senderId,
        CancellationToken cancellationToken)
    {
        // A normal member may self-service share only when the freshly synced
        // linked poll proves that their UID is still in the selected option.
        // Post-draft poll sync is intentionally unavailable, so those cases keep
        // the existing operator requirement instead of trusting stale data.
        if (session.Status is not (SessionStatus.Setup or SessionStatus.CaptainSelection))
            return false;

        var latestImport = await db.PollImports
            .AsNoTracking()
            .Where(import => import.SessionId == session.Id)
            .OrderByDescending(import => import.ImportedAt)
            .Select(import => new { import.PollId, import.SelectedOptionIdsJson })
            .FirstOrDefaultAsync(cancellationToken);
        if (latestImport is null) return false;

        var normalizedSenderId = NormalizeId(senderId);
        var senderRows = await db.SessionPlayers
            .AsNoTracking()
            .Where(player => player.SessionId == session.Id &&
                             player.IsPresent &&
                             player.PlayerProfile != null)
            .Select(player => new
            {
                ZaloUserId = player.PlayerProfile!.ZaloUserId,
                player.SourcePollId,
                player.SourceOptionIdsJson
            })
            .ToListAsync(cancellationToken);
        var sender = senderRows.FirstOrDefault(row =>
            NormalizeId(row.ZaloUserId) == normalizedSenderId);

        return sender is not null && HasCurrentPollVote(
            sender.SourcePollId,
            sender.SourceOptionIdsJson,
            latestImport.PollId,
            latestImport.SelectedOptionIdsJson);
    }

    internal static bool HasCurrentPollVote(
        string? sourcePollId,
        string? sourceOptionIdsJson,
        string? currentPollId,
        string? selectedOptionIdsJson)
    {
        if (string.IsNullOrWhiteSpace(sourcePollId) ||
            string.IsNullOrWhiteSpace(currentPollId) ||
            !string.Equals(sourcePollId, currentPollId, StringComparison.Ordinal))
            return false;

        try
        {
            var sourceIds = (JsonSerializer.Deserialize<List<string>>(sourceOptionIdsJson ?? \"[]\") ?? [])
                .Where(id => !string.IsNullOrWhiteSpace(id))
                .ToHashSet(StringComparer.Ordinal);
            if (sourceIds.Count == 0) return false;

            var selectedIds = (JsonSerializer.Deserialize<List<string>>(selectedOptionIdsJson ?? \"[]\") ?? [])
                .Where(id => !string.IsNullOrWhiteSpace(id))
                .ToHashSet(StringComparer.Ordinal);
            return sourceIds.Overlaps(selectedIds);
        }
        catch (JsonException)
        {
            return false;
        }
    }

    private async Task<BotAnswer> UpdatePlayerProfileAsync("""
if marker not in source:
    raise SystemExit("refresh insertion point not found")
source = source.replace(marker, helper, 1)

marker = """        var session = refresh.Session;
        var mentionedMembers = await ResolveZaloMembersAsync(
            session,
            mentionedUsers.Select(user => user.ZaloUserId),
            cancellationToken);"""
replacement = """        var session = refresh.Session;
        var senderIsCurrentPollVoter = requestedOwnSlot &&
                                       await IsSenderCurrentPollVoterAsync(
                                           session,
                                           incoming.SenderId,
                                           cancellationToken);
        if (requestedOwnSlot && !senderIsCurrentPollVoter)
        {
            var operatorDenial = await GetOperatorDenialAsync(
                session,
                incoming.SenderId,
                decision.Intent,
                aiCalled);
            if (operatorDenial is not null)
            {
                var message = session.Status is SessionStatus.Setup or SessionStatus.CaptainSelection
                    ? string.IsNullOrWhiteSpace(session.LatestPoll)
                        ? $\"Mình chưa có poll/option đã liên kết để xác minh vote hiện tại của {session.Name}. Thành viên thường chỉ tự share slot khi chính UID của mình đang vote option của trận này.\"
                        : $\"Mình đã đồng bộ vote hiện tại của {session.Name} nhưng UID của bạn không nằm trong option đang liên kết. Thành viên thường chỉ tự share slot khi chính mình đang vote option của trận này. Hãy vote đúng option rồi thử lại.\"
                    : $\"{session.Name} đã bắt đầu draft hoặc draft xong nên mình không thể xác minh vote hiện tại an toàn cho self-service. Hãy nhờ trưởng nhóm, phó nhóm hoặc operator thực hiện.\";
                return new BotAnswer(
                    message,
                    null,
                    decision.Intent,
                    aiCalled,
                    ProtectedTerms: [session.Name]);
            }
        }
        var mentionedMembers = await ResolveZaloMembersAsync(
            session,
            mentionedUsers.Select(user => user.ZaloUserId),
            cancellationToken);"""
if marker not in source:
    raise SystemExit("share session insertion point not found")
source = source.replace(marker, replacement, 1)

marker = """        var selfService = session.SenderIsListed &&
                          !string.IsNullOrWhiteSpace(session.SenderPlayerName) &&
                          resolvedAnchor is not null &&
                          NormalizeText(resolvedAnchor) == NormalizeText(session.SenderPlayerName);"""
replacement = """        var selfService = senderIsCurrentPollVoter &&
                          session.SenderIsListed &&
                          !string.IsNullOrWhiteSpace(session.SenderPlayerName) &&
                          resolvedAnchor is not null &&
                          NormalizeText(resolvedAnchor) == NormalizeText(session.SenderPlayerName);"""
if marker not in source:
    raise SystemExit("self-service gate not found")
source = source.replace(marker, replacement, 1)

marker = """        session = refresh.Session;

        var selfStillValid = plan.SelfService &&
                             session.SenderIsListed &&
                             !string.IsNullOrWhiteSpace(session.SenderPlayerName) &&
                             NormalizeText(plan.AnchorPlayerName) == NormalizeText(session.SenderPlayerName);"""
replacement = """        session = refresh.Session;

        var senderIsCurrentPollVoter = plan.SelfService &&
                                       await IsSenderCurrentPollVoterAsync(
                                           session,
                                           incoming.SenderId,
                                           cancellationToken);
        if (plan.SelfService && !senderIsCurrentPollVoter)
        {
            var operatorDenial = await GetOperatorDenialAsync(
                session,
                incoming.SenderId,
                ZaloBotIntent.ShareSlotConfirm,
                aiCalled);
            if (operatorDenial is not null)
            {
                return new BotAnswer(
                    $\"Bạn không còn nằm trong vote hiện tại của option đang liên kết cho {session.Name}, nên mình không áp dụng share slot self-service. Hãy vote lại đúng option rồi gửi lại yêu cầu; trưởng/phó nhóm hoặc operator vẫn có thể xử lý thay.\",
                    null,
                    ZaloBotIntent.ShareSlotConfirm,
                    aiCalled,
                    ProtectedTerms: [session.Name]);
            }
        }
        var selfStillValid = plan.SelfService &&
                             senderIsCurrentPollVoter &&
                             session.SenderIsListed &&
                             !string.IsNullOrWhiteSpace(session.SenderPlayerName) &&
                             NormalizeText(plan.AnchorPlayerName) == NormalizeText(session.SenderPlayerName);"""
if marker not in source:
    raise SystemExit("confirmation self-service gate not found")
source = source.replace(marker, replacement, 1)

path.write_text(source, encoding="utf-8")

Path("server/VolleyDraft.Api.Tests/ZaloBotShareSelfServiceTests.cs").write_text(
    '''using VolleyDraft.Api.Services;\nusing Xunit;\n\nnamespace VolleyDraft.Api.Tests;\n\npublic sealed class ZaloBotShareSelfServiceTests\n{\n    [Fact]\n    public void Current_vote_in_selected_option_is_accepted()\n    {\n        Assert.True(ZaloBotService.HasCurrentPollVote(\n            "poll-1", "[\\\"option-cn\\\"]", "poll-1", "[\\\"option-cn\\\"]"));\n    }\n\n    [Fact]\n    public void Vote_from_different_poll_is_rejected()\n    {\n        Assert.False(ZaloBotService.HasCurrentPollVote(\n            "poll-old", "[\\\"option-cn\\\"]", "poll-current", "[\\\"option-cn\\\"]"));\n    }\n\n    [Fact]\n    public void Vote_from_unlinked_option_is_rejected()\n    {\n        Assert.False(ZaloBotService.HasCurrentPollVote(\n            "poll-1", "[\\\"option-t6\\\"]", "poll-1", "[\\\"option-cn\\\"]"));\n    }\n\n    [Fact]\n    public void Multiple_selected_options_accept_any_intersection()\n    {\n        Assert.True(ZaloBotService.HasCurrentPollVote(\n            "poll-1", "[\\\"option-cn\\\"]", "poll-1", "[\\\"option-t6\\\",\\\"option-cn\\\"]"));\n    }\n\n    [Fact]\n    public void Malformed_option_json_is_rejected_safely()\n    {\n        Assert.False(ZaloBotService.HasCurrentPollVote(\n            "poll-1", "not-json", "poll-1", "[\\\"option-cn\\\"]"));\n    }\n}\n''',
    encoding="utf-8",
)
print("share self-service patch applied")
