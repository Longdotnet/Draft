using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.EntityFrameworkCore;
using VolleyDraft.Api.Contracts;
using VolleyDraft.Api.Data;
using VolleyDraft.Api.Models;

namespace VolleyDraft.Api.Services;

public sealed record ZaloMemberBotAnswer(
    string Text,
    ZaloBotIntent Intent,
    bool AiCalled = false,
    IReadOnlyList<string>? ProtectedTerms = null);

public sealed partial class ZaloMemberIntelligenceBotService(
    VolleyDraftDbContext db,
    ZaloMemberActivityService activity,
    ZaloActivityBackfillCoordinator backfill,
    ZaloIntegrationService zaloIntegration,
    AiAssistantService ai,
    ILogger<ZaloMemberIntelligenceBotService> logger)
{
    private static readonly TimeSpan VietnamOffset = TimeSpan.FromHours(7);
    private static readonly HashSet<ZaloBotIntent> ActivityIntents =
    [
        ZaloBotIntent.ListMembersWithoutRecentVote,
        ZaloBotIntent.ListMembersWithoutRecentMessage,
        ZaloBotIntent.GetMemberLastActivity,
        ZaloBotIntent.GetMemberLastVote,
        ZaloBotIntent.GetMemberLastMessage,
        ZaloBotIntent.AnalyzeMemberVoteActivity,
        ZaloBotIntent.AnalyzeMemberMessageActivity,
        ZaloBotIntent.AnalyzeGroupEngagement,
        ZaloBotIntent.ListMostInactiveMembers,
        ZaloBotIntent.ListAtRiskMembers,
        ZaloBotIntent.SyncMemberActivity,
        ZaloBotIntent.GetActivitySyncStatus
    ];
    private const string PendingCount = "MemberActivity:Count";
    private const string PendingMember = "MemberActivity:Member";
    private const string PendingPagination = "MemberActivity:Pagination";

    public async Task<ZaloMemberBotAnswer?> TryHandleAsync(
        string connectionId,
        string groupId,
        ZaloIncomingMessageEvent incoming,
        string question,
        CancellationToken cancellationToken)
    {
        var senderId = NormalizeId(incoming.SenderId);
        var state = await db.ZaloBotConversationStates
            .SingleOrDefaultAsync(item =>
                item.ZaloConnectionId == connectionId &&
                item.GroupId == groupId &&
                item.SenderZaloUserId == senderId &&
                item.ExpiresAt > DateTimeOffset.UtcNow,
                cancellationToken);
        if (state is not null && state.PendingIntent.StartsWith("MemberActivity:", StringComparison.Ordinal))
        {
            var pending = await HandlePendingAsync(
                state,
                connectionId,
                groupId,
                incoming,
                question,
                cancellationToken);
            if (pending.Handled)
                return pending.Answer;
        }

        var deterministic = ZaloBotIntelligence.ClassifyDeterministically(question);
        var decision = deterministic.Intent;
        var aiCalled = false;
        ZaloMemberActivityClassification? classification = null;
        if (!ActivityIntents.Contains(decision) && LooksLikeActivityQuestion(question) && ai.IsConfigured)
        {
            classification = await ai.ClassifyMemberActivityAsync(
                new ZaloMemberActivityClassifierContext(
                    question,
                    senderId,
                    incoming.SenderName,
                    DateTimeOffset.UtcNow.ToOffset(VietnamOffset)),
                cancellationToken);
            aiCalled = true;
            if (classification is { Confidence: >= .72 } &&
                ActivityIntents.Contains(classification.Intent))
                decision = classification.Intent;
        }
        else if (ActivityIntents.Contains(decision) && ai.IsConfigured &&
                 !ZaloBotIntelligence.TryGetExactCommand(question, out _))
        {
            // AI only extracts optional person/time/limit. The deterministic route
            // remains authoritative if the free model returns malformed JSON.
            classification = await ai.ClassifyMemberActivityAsync(
                new ZaloMemberActivityClassifierContext(
                    question,
                    senderId,
                    incoming.SenderName,
                    DateTimeOffset.UtcNow.ToOffset(VietnamOffset)),
                cancellationToken);
            aiCalled = true;
        }

        if (!ActivityIntents.Contains(decision))
            return null;

        var period = BuildPeriod(question, classification?.TimeRange);
        var limit = classification?.Limit;
        if (decision == ZaloBotIntent.ListMostInactiveMembers &&
            ZaloBotIntelligence.TryGetExactCommand(question, out var command) &&
            command == 12)
        {
            var denial = await GetOperatorDenialAsync(connectionId, groupId, senderId, decision, cancellationToken);
            if (denial is not null) return denial;
            await SaveStateAsync(
                connectionId,
                groupId,
                senderId,
                PendingCount,
                new MemberActivityPendingPayload(
                    decision,
                    period.Start,
                    period.End,
                    period.Description,
                    null,
                    null,
                    1,
                    10,
                    null),
                cancellationToken);
            return new ZaloMemberBotAnswer(
                "Bạn muốn xem bao nhiêu người ít hoạt động nhất? Gửi một số từ 1 đến 30, ví dụ `@bot 10`.",
                decision,
                aiCalled);
        }

        if (decision == ZaloBotIntent.ListMostInactiveMembers && limit is null)
            limit = ExtractLimit(question) ?? 10;

        return await ExecuteAsync(
            connectionId,
            groupId,
            incoming,
            decision,
            period,
            classification?.MemberReference,
            limit,
            1,
            Math.Clamp(limit ?? 10, 1, 30),
            aiCalled,
            cancellationToken);
    }

    private async Task<PendingHandleResult> HandlePendingAsync(
        ZaloBotConversationState state,
        string connectionId,
        string groupId,
        ZaloIncomingMessageEvent incoming,
        string question,
        CancellationToken cancellationToken)
    {
        if (ZaloBotIntelligence.IsCancel(question))
        {
            db.ZaloBotConversationStates.Remove(state);
            await db.SaveChangesAsync(cancellationToken);
            return PendingHandleResult.With(new ZaloMemberBotAnswer(
                "Đã huỷ yêu cầu xem hoạt động đang chờ.",
                ZaloBotIntent.GeneralChat));
        }

        MemberActivityPendingPayload? payload;
        try
        {
            payload = JsonSerializer.Deserialize<MemberActivityPendingPayload>(state.PendingPayloadJson);
        }
        catch (JsonException)
        {
            payload = null;
        }
        if (payload is null)
        {
            db.ZaloBotConversationStates.Remove(state);
            await db.SaveChangesAsync(cancellationToken);
            return PendingHandleResult.NotHandled;
        }

        if (state.PendingIntent == PendingCount &&
            TryReadCount(question, out var count))
        {
            db.ZaloBotConversationStates.Remove(state);
            await db.SaveChangesAsync(cancellationToken);
            return PendingHandleResult.With(await ExecuteAsync(
                connectionId,
                groupId,
                incoming,
                payload.Intent,
                payload.ToPeriod(),
                payload.MemberReference,
                count,
                1,
                count,
                false,
                cancellationToken));
        }

        if (state.PendingIntent == PendingMember &&
            TryReadCount(question, out var memberNumber) &&
            payload.CandidateUserIds is { Count: > 0 } &&
            memberNumber <= payload.CandidateUserIds.Count)
        {
            var selectedUserId = payload.CandidateUserIds[memberNumber - 1];
            db.ZaloBotConversationStates.Remove(state);
            await db.SaveChangesAsync(cancellationToken);
            return PendingHandleResult.With(await ExecuteForMemberAsync(
                connectionId,
                groupId,
                incoming,
                payload.Intent,
                payload.ToPeriod(),
                selectedUserId,
                false,
                cancellationToken));
        }

        if (state.PendingIntent == PendingPagination &&
            TryResolvePage(question, payload.Page, payload.TotalPages ?? 1, out var targetPage))
        {
            db.ZaloBotConversationStates.Remove(state);
            await db.SaveChangesAsync(cancellationToken);
            return PendingHandleResult.With(await ExecuteListAsync(
                connectionId,
                groupId,
                incoming,
                payload.Intent,
                payload.ToPeriod(),
                payload.Limit,
                targetPage,
                payload.PageSize,
                false,
                cancellationToken));
        }

        var current = ZaloBotIntelligence.ClassifyDeterministically(question);
        if (ActivityIntents.Contains(current.Intent))
        {
            db.ZaloBotConversationStates.Remove(state);
            await db.SaveChangesAsync(cancellationToken);
            return PendingHandleResult.NotHandled;
        }

        // Do not let an activity pagination/clarification state hijack an unrelated
        // existing bot command such as draft, reminder or waitlist.
        db.ZaloBotConversationStates.Remove(state);
        await db.SaveChangesAsync(cancellationToken);
        return PendingHandleResult.NotHandled;
    }

    private async Task<ZaloMemberBotAnswer> ExecuteAsync(
        string connectionId,
        string groupId,
        ZaloIncomingMessageEvent incoming,
        ZaloBotIntent intent,
        ZaloActivityPeriod period,
        string? memberReference,
        int? limit,
        int page,
        int pageSize,
        bool aiCalled,
        CancellationToken cancellationToken)
    {
        if (intent == ZaloBotIntent.GetActivitySyncStatus)
            return await GetSyncStatusAsync(connectionId, groupId, incoming.SenderId, intent, aiCalled, cancellationToken);
        if (intent == ZaloBotIntent.SyncMemberActivity)
        {
            var denial = await GetOperatorDenialAsync(
                connectionId,
                groupId,
                incoming.SenderId,
                intent,
                cancellationToken);
            if (denial is not null) return denial;
            var job = await backfill.QueueGroupAsync(connectionId, groupId, true, cancellationToken);
            return new ZaloMemberBotAnswer(
                $"Đã đưa yêu cầu đồng bộ dữ liệu Zalo cũ vào hàng đợi. Mã job: {job.Id[..8]}. Bạn có thể hỏi `@bot đồng bộ tới đâu rồi?` để xem tiến độ.",
                intent,
                aiCalled,
                [job.Id[..8]]);
        }

        var readiness = await EnsureReadyAsync(connectionId, groupId, cancellationToken);
        if (readiness is not null)
            return readiness with { Intent = intent, AiCalled = aiCalled };

        if (intent is ZaloBotIntent.ListMembersWithoutRecentVote or
            ZaloBotIntent.ListMembersWithoutRecentMessage or
            ZaloBotIntent.ListMostInactiveMembers or
            ZaloBotIntent.ListAtRiskMembers)
        {
            return await ExecuteListAsync(
                connectionId,
                groupId,
                incoming,
                intent,
                period,
                limit,
                page,
                pageSize,
                aiCalled,
                cancellationToken);
        }

        if (intent == ZaloBotIntent.AnalyzeGroupEngagement)
        {
            var denial = await GetOperatorDenialAsync(
                connectionId,
                groupId,
                incoming.SenderId,
                intent,
                cancellationToken);
            if (denial is not null) return denial;
            var all = await activity.QueryGroupAsync(
                connectionId,
                groupId,
                period,
                ZaloMemberActivityFilter.All,
                1,
                5000,
                cancellationToken);
            var response =
                $"Tổng quan hoạt động {period.Description}:\n" +
                $"- Thành viên hiện tại: {all.TotalItems}\n" +
                $"- Có vote poll hợp lệ: {all.Items.Count(item => item.VotedPollCount > 0)}\n" +
                $"- Có nhắn trong nhóm: {all.Items.Count(item => item.MessageCount > 0)}\n" +
                $"- Chưa ghi nhận vote: {all.Items.Count(item => item.VotedPollCount == 0)}\n" +
                $"- Chưa ghi nhận tin nhắn: {all.Items.Count(item => item.MessageCount == 0)}";
            if (!string.IsNullOrWhiteSpace(all.Coverage.Warning))
                response += $"\n\nLưu ý dữ liệu: {all.Coverage.Warning}";
            return new ZaloMemberBotAnswer(
                response,
                intent,
                aiCalled,
                BuildProtectedTerms(all.Items, all.Coverage));
        }

        var memberResolution = await ResolveMemberAsync(
            connectionId,
            groupId,
            incoming,
            memberReference,
            intent,
            period,
            cancellationToken);
        if (memberResolution.Answer is not null)
            return memberResolution.Answer with { AiCalled = aiCalled };
        return await ExecuteForMemberAsync(
            connectionId,
            groupId,
            incoming,
            intent,
            period,
            memberResolution.UserId!,
            aiCalled,
            cancellationToken);
    }

    private async Task<ZaloMemberBotAnswer> ExecuteListAsync(
        string connectionId,
        string groupId,
        ZaloIncomingMessageEvent incoming,
        ZaloBotIntent intent,
        ZaloActivityPeriod period,
        int? requestedLimit,
        int page,
        int pageSize,
        bool aiCalled,
        CancellationToken cancellationToken)
    {
        var denial = await GetOperatorDenialAsync(
            connectionId,
            groupId,
            incoming.SenderId,
            intent,
            cancellationToken);
        if (denial is not null) return denial;

        var filter = intent switch
        {
            ZaloBotIntent.ListMembersWithoutRecentVote => ZaloMemberActivityFilter.NoVote,
            ZaloBotIntent.ListMembersWithoutRecentMessage => ZaloMemberActivityFilter.NoMessage,
            ZaloBotIntent.ListAtRiskMembers => ZaloMemberActivityFilter.AtRisk,
            _ => ZaloMemberActivityFilter.AtRisk
        };
        var limit = Math.Clamp(requestedLimit ?? 10, 1, 30);
        pageSize = Math.Clamp(Math.Min(pageSize, limit), 1, 10);
        var result = await activity.QueryGroupAsync(
            connectionId,
            groupId,
            period,
            filter,
            page,
            pageSize,
            cancellationToken);
        var cappedTotal = Math.Min(result.TotalItems, limit);
        var totalPages = Math.Max(1, (int)Math.Ceiling(cappedTotal / (double)pageSize));
        page = Math.Min(page, totalPages);
        var items = result.Items
            .Take(Math.Max(0, limit - ((page - 1) * pageSize)))
            .ToList();
        var label = intent switch
        {
            ZaloBotIntent.ListMembersWithoutRecentVote => "chưa được ghi nhận tham gia poll hợp lệ",
            ZaloBotIntent.ListMembersWithoutRecentMessage => "chưa được ghi nhận nhắn trong nhóm",
            ZaloBotIntent.ListAtRiskMembers => "có dấu hiệu giảm hoặc thiếu hoạt động",
            _ => "ít hoạt động nhất"
        };
        if (items.Count == 0)
        {
            return new ZaloMemberBotAnswer(
                $"Trong {period.Description}, chưa có thành viên hiện tại nào {label}.",
                intent,
                aiCalled);
        }

        var firstOrdinal = (page - 1) * pageSize + 1;
        var lines = items.Select((item, index) =>
            $"{firstOrdinal + index}. {item.DisplayName} — {FormatEvidence(item)}");
        var text =
            $"Trong {period.Description}, có {result.TotalItems} thành viên hiện tại {label}.\n" +
            $"Đang hiển thị {firstOrdinal}–{firstOrdinal + items.Count - 1}:\n\n" +
            string.Join("\n", lines);
        if (page < totalPages)
            text += "\n\nGõ `@bot tiếp` để xem trang sau.";
        if (!string.IsNullOrWhiteSpace(result.Coverage.Warning))
            text += $"\n\nLưu ý dữ liệu: {result.Coverage.Warning}";

        if (totalPages > 1)
        {
            await SaveStateAsync(
                connectionId,
                groupId,
                NormalizeId(incoming.SenderId),
                PendingPagination,
                new MemberActivityPendingPayload(
                    intent,
                    period.Start,
                    period.End,
                    period.Description,
                    null,
                    limit,
                    page,
                    pageSize,
                    null,
                    totalPages),
                cancellationToken);
        }

        return new ZaloMemberBotAnswer(
            text,
            intent,
            aiCalled,
            BuildProtectedTerms(items, result.Coverage));
    }

    private async Task<ZaloMemberBotAnswer> ExecuteForMemberAsync(
        string connectionId,
        string groupId,
        ZaloIncomingMessageEvent incoming,
        ZaloBotIntent intent,
        ZaloActivityPeriod period,
        string memberUserId,
        bool aiCalled,
        CancellationToken cancellationToken)
    {
        var senderId = NormalizeId(incoming.SenderId);
        if (!string.Equals(senderId, memberUserId, StringComparison.Ordinal))
        {
            var denial = await GetOperatorDenialAsync(
                connectionId,
                groupId,
                senderId,
                intent,
                cancellationToken);
            if (denial is not null) return denial;
        }

        var item = await activity.GetMemberActivityAsync(
            connectionId,
            groupId,
            memberUserId,
            period,
            cancellationToken);
        if (item is null)
            return new ZaloMemberBotAnswer(
                "Mình không tìm thấy UID thành viên hiện tại trong dữ liệu nhóm đã đồng bộ.",
                intent,
                aiCalled);

        string text;
        switch (intent)
        {
            case ZaloBotIntent.GetMemberLastVote:
                text = item.LastVotedPollCreatedAt is null
                    ? $"Chưa tìm thấy poll hợp lệ nào có {item.DisplayName} trong danh sách người vote."
                    : $"Poll gần nhất hệ thống tìm thấy {item.DisplayName} có tham gia là “{item.LastVotedPollQuestion}”, được tạo ngày {FormatDate(item.LastVotedPollCreatedAt.Value)}." +
                      $"\nHệ thống quan sát lựa chọn này lần đầu lúc {FormatDateTime(item.LastVotedPollFirstObservedAt)}; đây không phải thời điểm bấm vote chính xác vì Zalo không cung cấp timestamp theo từng người.";
                break;
            case ZaloBotIntent.GetMemberLastMessage:
                text = item.LastMessageAt is null
                    ? $"Chưa tìm thấy tin nhắn nào của {item.DisplayName} trong dữ liệu nhóm đã lưu."
                    : $"Tin nhắn gần nhất hệ thống ghi nhận của {item.DisplayName}: {FormatDateTime(item.LastMessageAt)}.";
                break;
            case ZaloBotIntent.AnalyzeMemberVoteActivity:
                text =
                    $"Phân tích vote của {item.DisplayName} trong {period.Description}:\n" +
                    $"- Poll hợp lệ: {item.EligiblePollCount}\n" +
                    $"- Poll có tham gia: {item.VotedPollCount}\n" +
                    $"- Tỷ lệ tham gia: {FormatPercent(item.VoteParticipationRate)}\n" +
                    $"- Tổng lựa chọn hiện còn hiệu lực: {item.TotalSelectedOptions}\n" +
                    $"- Bỏ lỡ liên tiếp gần đây: {item.ConsecutiveEligiblePollsMissed} poll\n" +
                    $"- Poll gần nhất: {(item.LastVotedPollCreatedAt is null ? "chưa tìm thấy" : $"“{item.LastVotedPollQuestion}”, tạo {FormatDate(item.LastVotedPollCreatedAt.Value)}")}\n" +
                    $"Xu hướng: {FormatTrend(item.Trend)}.";
                break;
            case ZaloBotIntent.AnalyzeMemberMessageActivity:
                text =
                    $"Hoạt động chat của {item.DisplayName} trong {period.Description}:\n" +
                    $"- Số tin nhắn: {item.MessageCount}\n" +
                    $"- Số ngày có nhắn: {item.ActiveMessageDays}\n" +
                    $"- Tin gần nhất: {(item.LastMessageAt is null ? "chưa tìm thấy" : FormatDateTime(item.LastMessageAt))}\n" +
                    $"- Trạng thái dữ liệu: {item.DataConfidence}.";
                break;
            default:
                text =
                    $"Hoạt động gần nhất ghi nhận của {item.DisplayName}:\n\n" +
                    $"- Tin nhắn gần nhất: {(item.LastMessageAt is null ? "chưa tìm thấy" : FormatDateTime(item.LastMessageAt))}\n" +
                    $"- Poll gần nhất có tham gia: {(item.LastVotedPollCreatedAt is null ? "chưa tìm thấy" : $"“{item.LastVotedPollQuestion}”, tạo ngày {FormatDate(item.LastVotedPollCreatedAt.Value)}")}\n" +
                    $"- Hoạt động mới nhất: {(item.LastActivityAt is null ? "chưa ghi nhận" : $"{FormatActivitySource(item.LastActivitySource)} lúc {FormatDateTime(item.LastActivityAt)}")}.";
                break;
        }

        var coverage = await activity.QueryGroupAsync(
            connectionId,
            groupId,
            period,
            ZaloMemberActivityFilter.All,
            1,
            1,
            cancellationToken);
        if ((intent is ZaloBotIntent.GetMemberLastMessage or
             ZaloBotIntent.AnalyzeMemberMessageActivity or
             ZaloBotIntent.GetMemberLastActivity) &&
            !string.IsNullOrWhiteSpace(coverage.Coverage.Warning))
            text += $"\n\nLưu ý dữ liệu: {coverage.Coverage.Warning}";
        return new ZaloMemberBotAnswer(
            text,
            intent,
            aiCalled,
            BuildProtectedTerms([item], coverage.Coverage));
    }

    private async Task<MemberResolution> ResolveMemberAsync(
        string connectionId,
        string groupId,
        ZaloIncomingMessageEvent incoming,
        string? memberReference,
        ZaloBotIntent intent,
        ZaloActivityPeriod period,
        CancellationToken cancellationToken)
    {
        var senderId = NormalizeId(incoming.SenderId);
        var normalizedQuestion = ZaloBotIntelligence.Normalize(incoming.Content);
        if (IsSelfReference(memberReference) ||
            (string.IsNullOrWhiteSpace(memberReference) &&
             SelfReferenceRegex().IsMatch(normalizedQuestion)))
            return MemberResolution.Found(senderId);

        var mentionedIds = incoming.Mentions
            .Where(mention => NormalizeId(mention.Uid) != NormalizeId(incoming.BotId))
            .Select(mention => NormalizeId(mention.Uid))
            .Distinct(StringComparer.Ordinal)
            .ToList();
        if (mentionedIds.Count == 1)
            return MemberResolution.Found(mentionedIds[0]);

        var members = await db.ZaloGroupMembers
            .AsNoTracking()
            .Where(member =>
                member.ZaloConnectionId == connectionId &&
                member.GroupId == groupId &&
                member.IsCurrentMember)
            .ToListAsync(cancellationToken);
        var aliases = await db.PlayerProfiles
            .AsNoTracking()
            .Where(profile => profile.ZaloUserId != null)
            .Select(profile => new { profile.ZaloUserId, profile.DisplayName })
            .ToListAsync(cancellationToken);

        var reference = ZaloBotIntelligence.Normalize(memberReference ?? string.Empty);
        IEnumerable<ZaloGroupMember> candidates;
        if (!string.IsNullOrWhiteSpace(reference))
        {
            candidates = members.Where(member =>
            {
                var name = ZaloBotIntelligence.Normalize(member.DisplayName);
                if (name == reference) return true;
                var aliasMatch = aliases.Any(alias =>
                    string.Equals(alias.ZaloUserId, member.ZaloUserId, StringComparison.Ordinal) &&
                    ZaloBotIntelligence.Normalize(alias.DisplayName) == reference);
                if (aliasMatch) return true;
                return reference.Length >= 3 &&
                       (ContainsWholePhrase(name, reference) ||
                        ZaloBotIntelligence.TokenSimilarity(name, reference) >= .86);
            });
        }
        else
        {
            candidates = members.Where(member =>
            {
                var name = ZaloBotIntelligence.Normalize(member.DisplayName);
                return name.Length >= 3 && ContainsWholePhrase(normalizedQuestion, name);
            });
            if (!candidates.Any())
            {
                var words = normalizedQuestion
                    .Split(' ', StringSplitOptions.RemoveEmptyEntries)
                    .Where(word => word.Length >= 3)
                    .ToHashSet(StringComparer.Ordinal);
                candidates = members.Where(member =>
                {
                    var nameTokens = ZaloBotIntelligence.Normalize(member.DisplayName)
                        .Split(' ', StringSplitOptions.RemoveEmptyEntries);
                    return nameTokens.Any(words.Contains);
                });
            }
        }

        var materialized = candidates
            .DistinctBy(member => member.ZaloUserId)
            .OrderBy(member => member.DisplayName)
            .Take(10)
            .ToList();
        if (materialized.Count == 1)
            return MemberResolution.Found(materialized[0].ZaloUserId);
        if (materialized.Count == 0)
            return MemberResolution.WithAnswer(new ZaloMemberBotAnswer(
                "Mình chưa xác định được bạn đang hỏi thành viên nào. Hãy @mention người đó hoặc ghi rõ tên.",
                intent));

        await SaveStateAsync(
            connectionId,
            groupId,
            senderId,
            PendingMember,
            new MemberActivityPendingPayload(
                intent,
                period.Start,
                period.End,
                period.Description,
                memberReference,
                null,
                1,
                10,
                materialized.Select(member => member.ZaloUserId).ToList()),
            cancellationToken);
        var choices = string.Join("\n", materialized.Select((member, index) =>
            $"{index + 1}. {member.DisplayName}"));
        return MemberResolution.WithAnswer(new ZaloMemberBotAnswer(
            $"Bạn đang hỏi người nào?\n{choices}\n\nTrả lời `@bot + số`, ví dụ `@bot 2`.",
            intent,
            ProtectedTerms: materialized.Select(member => member.DisplayName).ToList()));
    }

    private async Task<ZaloMemberBotAnswer?> GetOperatorDenialAsync(
        string connectionId,
        string groupId,
        string senderId,
        ZaloBotIntent intent,
        CancellationToken cancellationToken)
    {
        senderId = NormalizeId(senderId);
        var sessions = await db.MatchSessions
            .AsNoTracking()
            .Where(session =>
                session.ZaloConnectionId == connectionId &&
                session.ZaloGroupId == groupId &&
                session.BotEnabled)
            .Select(session => new OperatorSession(
                session.Id,
                session.AdminUserId,
                session.BotOperatorZaloUserIdsJson))
            .ToListAsync(cancellationToken);
        if (sessions.Any(session => ParseIds(session.OperatorIdsJson).Contains(senderId)))
            return null;
        var roleSession = sessions.FirstOrDefault();
        if (roleSession is not null)
        {
            var role = await zaloIntegration.GetGroupRoleAuthorizationAsync(
                roleSession.AdminUserId,
                roleSession.SessionId,
                senderId);
            if (role.IsSuccess && role.Value?.CanOperateBot == true)
                return null;
            if (!role.IsSuccess)
            {
                logger.LogWarning(
                    "Member analytics authorization could not verify group role ConnectionId={ConnectionId} GroupId={GroupId} SenderId={SenderId} Error={Error}",
                    connectionId,
                    groupId,
                    senderId,
                    role.Error);
                return new ZaloMemberBotAnswer(
                    "Mình chưa xác minh được quyền trưởng/phó nhóm lúc này. Bạn thử lại sau hoặc nhờ admin thêm UID vào danh sách bot operator.",
                    intent);
            }
        }

        logger.LogInformation(
            "Denied Zalo member analytics ConnectionId={ConnectionId} GroupId={GroupId} SenderId={SenderId} Intent={Intent}",
            connectionId,
            groupId,
            senderId,
            intent);
        return new ZaloMemberBotAnswer(
            "Thông tin hoạt động toàn nhóm hoặc của người khác chỉ dành cho trưởng nhóm, phó nhóm và bot operator. Bạn vẫn có thể hỏi hoạt động của chính mình.",
            intent);
    }

    private async Task<ZaloMemberBotAnswer?> EnsureReadyAsync(
        string connectionId,
        string groupId,
        CancellationToken cancellationToken)
    {
        var job = await db.ZaloActivityBackfillJobs
            .AsNoTracking()
            .SingleOrDefaultAsync(item =>
                item.ZaloConnectionId == connectionId &&
                item.GroupId == groupId,
                cancellationToken);
        if (job is null)
        {
            job = await backfill.QueueGroupAsync(connectionId, groupId, true, cancellationToken);
            return new ZaloMemberBotAnswer(
                "Mình vừa bắt đầu đồng bộ thành viên, poll và lịch sử Zalo mà tài khoản hiện tại có thể truy cập. Bạn hỏi lại sau khi job chạy xong nhé.",
                ZaloBotIntent.GetActivitySyncStatus);
        }
        if (job.Status is ZaloActivityBackfillStatus.Queued or
            ZaloActivityBackfillStatus.Running or
            ZaloActivityBackfillStatus.FailedRetryable)
        {
            return new ZaloMemberBotAnswer(
                $"Đang đồng bộ dữ liệu Zalo cũ, hiện ở bước {FormatStage(job.Stage)} và đã xử lý {job.ProcessedCount}/{job.DiscoveredTotal?.ToString() ?? "?"} mục board. Bạn thử hỏi lại sau khi đồng bộ hoàn tất.",
                ZaloBotIntent.GetActivitySyncStatus,
                ProtectedTerms:
                [
                    job.ProcessedCount.ToString(CultureInfo.InvariantCulture),
                    job.DiscoveredTotal?.ToString(CultureInfo.InvariantCulture) ?? "?"
                ]);
        }
        if (job.Status == ZaloActivityBackfillStatus.FailedPermanent)
        {
            return new ZaloMemberBotAnswer(
                $"Đồng bộ dữ liệu đã dừng sau nhiều lần lỗi: {job.LastErrorSummary ?? "không rõ nguyên nhân"}. Admin có thể bấm thử lại trên web hoặc hỏi `@bot đồng bộ lại dữ liệu cũ`.",
                ZaloBotIntent.GetActivitySyncStatus);
        }
        return null;
    }

    private async Task<ZaloMemberBotAnswer> GetSyncStatusAsync(
        string connectionId,
        string groupId,
        string senderId,
        ZaloBotIntent intent,
        bool aiCalled,
        CancellationToken cancellationToken)
    {
        var denial = await GetOperatorDenialAsync(
            connectionId,
            groupId,
            senderId,
            intent,
            cancellationToken);
        if (denial is not null) return denial;
        var job = await db.ZaloActivityBackfillJobs
            .AsNoTracking()
            .SingleOrDefaultAsync(item =>
                item.ZaloConnectionId == connectionId &&
                item.GroupId == groupId,
                cancellationToken);
        if (job is null)
            return new ZaloMemberBotAnswer(
                "Nhóm này chưa có job đồng bộ hoạt động. Gõ `@bot đồng bộ lại dữ liệu cũ` để bắt đầu.",
                intent,
                aiCalled);
        var text =
            $"Trạng thái đồng bộ: {FormatStatus(job.Status)}\n" +
            $"- Bước hiện tại: {FormatStage(job.Stage)}\n" +
            $"- Thành viên hiện tại: {job.MembersSynchronized}\n" +
            $"- Board đã xử lý: {job.ProcessedCount}/{job.DiscoveredTotal?.ToString() ?? "?"}\n" +
            $"- Poll tìm thấy: {job.TotalPollsDiscovered}\n" +
            $"- Poll có UID người vote: {job.TotalPollsWithVoterIdentities}\n" +
            $"- Tin nhắn cũ nhập được: {job.MessagesImported}\n" +
            $"- Khả năng lịch sử chat: {job.MessageHistoryCapability}";
        if (!string.IsNullOrWhiteSpace(job.LastErrorSummary))
            text += $"\n- Giới hạn/lỗi gần nhất: {job.LastErrorSummary}";
        return new ZaloMemberBotAnswer(
            text,
            intent,
            aiCalled,
            [
                job.MembersSynchronized.ToString(CultureInfo.InvariantCulture),
                job.ProcessedCount.ToString(CultureInfo.InvariantCulture),
                job.TotalPollsDiscovered.ToString(CultureInfo.InvariantCulture),
                job.MessagesImported.ToString(CultureInfo.InvariantCulture)
            ]);
    }

    private async Task SaveStateAsync(
        string connectionId,
        string groupId,
        string senderId,
        string pendingIntent,
        MemberActivityPendingPayload payload,
        CancellationToken cancellationToken)
    {
        var state = await db.ZaloBotConversationStates
            .SingleOrDefaultAsync(item =>
                item.ZaloConnectionId == connectionId &&
                item.GroupId == groupId &&
                item.SenderZaloUserId == senderId,
                cancellationToken);
        var now = DateTimeOffset.UtcNow;
        if (state is null)
        {
            state = new ZaloBotConversationState
            {
                ZaloConnectionId = connectionId,
                GroupId = groupId,
                SenderZaloUserId = senderId,
                CreatedAt = now
            };
            db.ZaloBotConversationStates.Add(state);
        }
        state.PendingIntent = pendingIntent;
        state.PendingPayloadJson = JsonSerializer.Serialize(payload);
        state.PreviousCommand = payload.Intent.ToString();
        state.ExpiresAt = now.AddMinutes(15);
        state.UpdatedAt = now;
        await db.SaveChangesAsync(cancellationToken);
    }

    private static ZaloActivityPeriod BuildPeriod(
        string question,
        ZaloActivityTimeRangeExtraction? extraction)
    {
        if (extraction is null)
            return ZaloMemberActivityService.ParsePeriod(question);
        var localNow = DateTimeOffset.UtcNow.ToOffset(VietnamOffset);
        var today = DateOnly.FromDateTime(localNow.DateTime);
        DateOnly start;
        DateOnly end = today.AddDays(1);
        string description;
        switch (extraction.Kind)
        {
            case ZaloActivityTimeRangeKind.PreviousDays:
                start = today.AddDays(-(extraction.Amount ?? 90));
                description = $"{extraction.Amount ?? 90} ngày gần đây";
                break;
            case ZaloActivityTimeRangeKind.PreviousCalendarMonths:
                start = today.AddMonths(-(extraction.Amount ?? 4));
                description = $"{extraction.Amount ?? 4} tháng gần đây";
                break;
            case ZaloActivityTimeRangeKind.ThisWeek:
                start = today.AddDays(-(((int)today.DayOfWeek + 6) % 7));
                description = "tuần này";
                break;
            case ZaloActivityTimeRangeKind.ThisMonth:
                start = new DateOnly(today.Year, today.Month, 1);
                description = "tháng này";
                break;
            case ZaloActivityTimeRangeKind.ThisYear:
                start = new DateOnly(today.Year, 1, 1);
                description = "năm nay";
                break;
            case ZaloActivityTimeRangeKind.ExplicitRange when
                extraction.StartDate is not null && extraction.EndDate is not null:
                start = extraction.StartDate.Value;
                end = extraction.EndDate.Value.AddDays(1);
                description = $"từ {start:dd/MM/yyyy} đến {extraction.EndDate:dd/MM/yyyy}";
                break;
            case ZaloActivityTimeRangeKind.SinceMonth when extraction.Amount is >= 1 and <= 12:
                var year = extraction.Amount <= today.Month ? today.Year : today.Year - 1;
                start = new DateOnly(year, extraction.Amount.Value, 1);
                description = $"từ tháng {extraction.Amount}";
                break;
            default:
                return ZaloMemberActivityService.ParsePeriod(question);
        }

        return new ZaloActivityPeriod(
            new DateTimeOffset(start.ToDateTime(TimeOnly.MinValue), VietnamOffset).ToUniversalTime(),
            new DateTimeOffset(end.ToDateTime(TimeOnly.MinValue), VietnamOffset).ToUniversalTime(),
            description);
    }

    private static string FormatEvidence(ZaloMemberActivityResponse item)
    {
        if (item.IsNewMember && item.LastActivityAt is null)
            return $"thành viên mới (thấy từ {FormatDate(item.FirstSeenAt)}), chưa đủ dữ liệu";
        var evidence = new List<string>();
        if (item.LastVotedPollCreatedAt is not null)
            evidence.Add($"poll gần nhất “{item.LastVotedPollQuestion}” tạo {FormatDate(item.LastVotedPollCreatedAt.Value)}");
        if (item.LastMessageAt is not null)
            evidence.Add($"nhắn gần nhất {FormatDateTime(item.LastMessageAt)}");
        return evidence.Count == 0
            ? "chưa tìm thấy vote hoặc tin nhắn trong dữ liệu đã quét"
            : string.Join("; ", evidence);
    }

    private static IReadOnlyList<string> BuildProtectedTerms(
        IReadOnlyList<ZaloMemberActivityResponse> items,
        ZaloActivityCoverageResponse coverage)
    {
        var terms = new List<string>();
        terms.AddRange(items.Select(item => item.DisplayName));
        terms.AddRange(items
            .Where(item => !string.IsNullOrWhiteSpace(item.LastVotedPollQuestion))
            .Select(item => item.LastVotedPollQuestion!));
        terms.Add(items.Count.ToString(CultureInfo.InvariantCulture));
        terms.Add(coverage.EligiblePollCount.ToString(CultureInfo.InvariantCulture));
        return terms.Distinct(StringComparer.Ordinal).ToList();
    }

    private static bool LooksLikeActivityQuestion(string question)
    {
        var q = ZaloBotIntelligence.Normalize(question);
        return q == "12" ||
               q.Contains("hoat dong", StringComparison.Ordinal) ||
               q.Contains("inactive", StringComparison.Ordinal) ||
               q.Contains("it tuong tac", StringComparison.Ordinal) ||
               q.Contains("im lang", StringComparison.Ordinal) ||
               ((q.Contains("vote", StringComparison.Ordinal) ||
                 q.Contains("poll", StringComparison.Ordinal) ||
                 q.Contains("binh chon", StringComparison.Ordinal) ||
                 q.Contains("nhan tin", StringComparison.Ordinal)) &&
                (q.Contains("gan nhat", StringComparison.Ordinal) ||
                 q.Contains("lan cuoi", StringComparison.Ordinal) ||
                 q.Contains("chua", StringComparison.Ordinal) ||
                 q.Contains("khong", StringComparison.Ordinal) ||
                 q.Contains("phan tich", StringComparison.Ordinal)));
    }

    private static bool TryReadCount(string question, out int count)
    {
        count = 0;
        var match = CountRegex().Match(ZaloBotIntelligence.Normalize(question));
        return match.Success &&
               int.TryParse(match.Groups[1].Value, NumberStyles.None, CultureInfo.InvariantCulture, out count) &&
               count is >= 1 and <= 30;
    }

    private static int? ExtractLimit(string question)
    {
        var q = ZaloBotIntelligence.Normalize(question);
        var match = Regex.Match(q, @"\btop\s*(\d{1,2})\b", RegexOptions.CultureInvariant);
        return match.Success &&
               int.TryParse(match.Groups[1].Value, out var limit) &&
               limit is >= 1 and <= 30
            ? limit
            : null;
    }

    private static bool TryResolvePage(
        string question,
        int currentPage,
        int totalPages,
        out int page)
    {
        var q = ZaloBotIntelligence.Normalize(question);
        if (q is "tiep" or "xem them" or "trang sau")
        {
            page = Math.Min(totalPages, currentPage + 1);
            return true;
        }
        if (q is "trang truoc" or "quay lai")
        {
            page = Math.Max(1, currentPage - 1);
            return true;
        }
        if (q is "dau" or "trang dau")
        {
            page = 1;
            return true;
        }
        if (q is "cuoi" or "trang cuoi")
        {
            page = totalPages;
            return true;
        }
        var match = PageRegex().Match(q);
        if (match.Success && int.TryParse(match.Groups[1].Value, out var requested))
        {
            page = Math.Clamp(requested, 1, totalPages);
            return true;
        }
        page = currentPage;
        return false;
    }

    private static bool IsSelfReference(string? reference)
    {
        var q = ZaloBotIntelligence.Normalize(reference ?? string.Empty);
        return q is "tui" or "minh" or "toi" or "em" or "ban than" or "chinh minh";
    }

    private static bool ContainsWholePhrase(string value, string phrase) =>
        Regex.IsMatch(
            value,
            $@"(?<![a-z0-9]){Regex.Escape(phrase)}(?![a-z0-9])",
            RegexOptions.CultureInvariant);

    private static IReadOnlySet<string> ParseIds(string? json)
    {
        try
        {
            return (JsonSerializer.Deserialize<List<string>>(json ?? "[]") ?? [])
                .Select(NormalizeId)
                .Where(id => id.Length > 0)
                .ToHashSet(StringComparer.Ordinal);
        }
        catch (JsonException)
        {
            return new HashSet<string>(StringComparer.Ordinal);
        }
    }

    private static string NormalizeId(string? value)
    {
        var normalized = value?.Trim() ?? string.Empty;
        return normalized.EndsWith("_0", StringComparison.Ordinal)
            ? normalized[..^2]
            : normalized;
    }

    private static string FormatDate(DateTimeOffset value) =>
        value.ToOffset(VietnamOffset).ToString("dd/MM/yyyy", CultureInfo.InvariantCulture);

    private static string FormatDateTime(DateTimeOffset? value) =>
        value is null
            ? "chưa tìm thấy"
            : value.Value.ToOffset(VietnamOffset).ToString("HH:mm 'ngày' dd/MM/yyyy", CultureInfo.InvariantCulture);

    private static string FormatPercent(double? value) =>
        value is null ? "chưa đủ dữ liệu" : value.Value.ToString("P1", CultureInfo.GetCultureInfo("vi-VN"));

    private static string FormatTrend(string trend) => trend switch
    {
        "Increasing" => "tỷ lệ tham gia gần đây tăng",
        "Decreasing" => "tỷ lệ tham gia gần đây giảm so với giai đoạn trước",
        "Stable" => "mức tham gia tương đối ổn định",
        _ => "chưa đủ dữ liệu để so sánh hai giai đoạn"
    };

    private static string FormatActivitySource(string? source) =>
        source == "Message" ? "tin nhắn" : source == "Poll" ? "poll" : "hoạt động";

    private static string FormatStage(ZaloActivityBackfillStage stage) => stage switch
    {
        ZaloActivityBackfillStage.Queued => "đang chờ",
        ZaloActivityBackfillStage.SyncingMembers => "đồng bộ thành viên",
        ZaloActivityBackfillStage.ScanningBoard => "quét board",
        ZaloActivityBackfillStage.SyncingPollDetails => "đồng bộ chi tiết poll",
        ZaloActivityBackfillStage.ProbingMessageHistory => "kiểm tra lịch sử chat",
        ZaloActivityBackfillStage.ImportingMessages => "nhập tin nhắn cũ",
        ZaloActivityBackfillStage.RebuildingMetrics => "tính lại chỉ số",
        _ => "hoàn tất"
    };

    private static string FormatStatus(ZaloActivityBackfillStatus status) => status switch
    {
        ZaloActivityBackfillStatus.Queued => "đang chờ",
        ZaloActivityBackfillStatus.Running => "đang chạy",
        ZaloActivityBackfillStatus.Completed => "hoàn tất",
        ZaloActivityBackfillStatus.CompletedWithLimitations => "hoàn tất nhưng có giới hạn",
        ZaloActivityBackfillStatus.FailedRetryable => "lỗi tạm thời, sẽ thử lại",
        _ => "đã dừng vì lỗi"
    };

    [GeneratedRegex(@"(?:^|\s)(\d{1,2})(?:\s*nguoi)?(?:\s|$)", RegexOptions.CultureInvariant)]
    private static partial Regex CountRegex();

    [GeneratedRegex(@"(?:^|\s)trang\s+(\d+)(?:\s|$)", RegexOptions.CultureInvariant)]
    private static partial Regex PageRegex();

    [GeneratedRegex(@"(?:^|\s)(?:tui|minh|toi|em|ban than|chinh minh)(?:\s|$)", RegexOptions.CultureInvariant)]
    private static partial Regex SelfReferenceRegex();

    private sealed record OperatorSession(
        string SessionId,
        string AdminUserId,
        string OperatorIdsJson);

    private sealed record MemberActivityPendingPayload(
        ZaloBotIntent Intent,
        DateTimeOffset PeriodStart,
        DateTimeOffset PeriodEnd,
        string PeriodDescription,
        string? MemberReference,
        int? Limit,
        int Page,
        int PageSize,
        IReadOnlyList<string>? CandidateUserIds,
        int? TotalPages = null)
    {
        public ZaloActivityPeriod ToPeriod() =>
            new(PeriodStart, PeriodEnd, PeriodDescription);
    }

    private sealed record PendingHandleResult(bool Handled, ZaloMemberBotAnswer? Answer)
    {
        public static PendingHandleResult NotHandled { get; } = new(false, null);
        public static PendingHandleResult With(ZaloMemberBotAnswer answer) => new(true, answer);
    }

    private sealed record MemberResolution(string? UserId, ZaloMemberBotAnswer? Answer)
    {
        public static MemberResolution Found(string userId) => new(userId, null);
        public static MemberResolution WithAnswer(ZaloMemberBotAnswer answer) => new(null, answer);
    }
}
