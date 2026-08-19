using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.EntityFrameworkCore;
using VolleyDraft.Api.Contracts;
using VolleyDraft.Api.Data;
using VolleyDraft.Api.Models;

namespace VolleyDraft.Api.Services;

internal sealed class ZaloAutoSessionConversationService(
    VolleyDraftDbContext db,
    ZaloBridgeClient bridge,
    ZaloCredentialProtector protector,
    ZaloAutoSessionConversationInterpreter interpreter,
    IServiceProvider serviceProvider,
    IConfiguration configuration,
    ILogger<ZaloAutoSessionConversationService> logger)
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    public static ZaloAutoSessionConversationService Create(IServiceProvider services)
    {
        var loggerFactory = services.GetRequiredService<ILoggerFactory>();
        var interpreter = new ZaloAutoSessionConversationInterpreter(
            services.GetRequiredService<IHttpClientFactory>(),
            services.GetRequiredService<IConfiguration>(),
            loggerFactory.CreateLogger<ZaloAutoSessionConversationInterpreter>());
        return new ZaloAutoSessionConversationService(
            services.GetRequiredService<VolleyDraftDbContext>(),
            services.GetRequiredService<ZaloBridgeClient>(),
            services.GetRequiredService<ZaloCredentialProtector>(),
            interpreter,
            services,
            services.GetRequiredService<IConfiguration>(),
            loggerFactory.CreateLogger<ZaloAutoSessionConversationService>());
    }
    private readonly ZaloAutoSessionConversationStore conversations = new(db);
    private readonly ZaloAutoSessionStore autoSessions = new(db);
    private readonly ZaloAutoSessionV2Store runtimeStore = new(db);

    public async Task ReconcileAsync(CancellationToken cancellationToken = default)
    {
        if (!configuration.GetValue("AutoSession:ConversationV3Enabled", true)) return;
        if (!(await runtimeStore.GetRuntimeAsync(cancellationToken)).GlobalEnabled) return;
        await EnsurePendingConversationsAsync(cancellationToken);
        await ProcessConversationHistoryAsync(cancellationToken);
        await ProcessFollowUpsAsync(cancellationToken);
    }

    public async Task<bool> TryHandleIncomingAsync(
        ZaloIncomingMessageEvent incoming,
        CancellationToken cancellationToken = default)
    {
        if (!configuration.GetValue("AutoSession:ConversationV3Enabled", true)) return false;

        var accountId = NormalizeId(incoming.AccountId);
        var groupId = NormalizeId(incoming.GroupId);
        var senderId = NormalizeId(incoming.SenderId);
        var messageId = NormalizeId(incoming.MessageId);
        if (accountId.Length == 0 || groupId.Length == 0 || senderId.Length == 0 || messageId.Length == 0)
            return false;

        await EnsurePendingConversationsAsync(cancellationToken);

        ZaloAutoSessionConversationData? conversation = null;
        var quotedMessageId = incoming.Quote?.MessageId?.Trim();
        var stronglyAddressed = incoming.MentionedBot;
        if (!string.IsNullOrWhiteSpace(quotedMessageId))
        {
            conversation = await conversations.FindByQuotedBotMessageAsync(groupId, quotedMessageId, cancellationToken);
            stronglyAddressed = stronglyAddressed || conversation is not null;
        }

        var active = conversation is null
            ? await conversations.GetActiveForGroupAsync(groupId, cancellationToken)
            : [];
        var implicitContext = false;

        if (conversation is null)
        {
            if (active.Count == 0) return false;
            if (active.Count > 1)
            {
                if (!incoming.MentionedBot) return false;
                await SendAmbiguousConversationMessageAsync(active[0], incoming, cancellationToken);
                return true;
            }

            conversation = active[0];
            if (!incoming.MentionedBot)
            {
                implicitContext = IsImplicitFollowUpWindow(conversation, senderId) &&
                                  LooksLikeImplicitConversationReply(incoming.Content);
                if (!implicitContext) return false;
            }
        }

        if (await conversations.HasTurnAsync(conversation.Id, messageId, cancellationToken))
            return true;

        if (conversation.ExpiresAt <= DateTimeOffset.UtcNow)
        {
            await ExpireAsync(conversation, "conversation_expired_on_message", cancellationToken);
            await SendConversationTextAsync(
                conversation,
                incoming.SenderId,
                incoming.SenderName,
                "Conversation của poll này đã hết hạn nên tui không tạo website từ câu trả lời này. Nếu vẫn cần lịch, hãy tạo poll mới hoặc nhờ admin mở lại quy trình.",
                cancellationToken);
            return true;
        }

        var tracked = await autoSessions.GetTrackedGroupAsync(conversation.TrackedGroupId, cancellationToken);
        if (tracked is null || !tracked.AutoSessionEnabled)
            return true;

        var connection = await GetConnectionAsync(tracked.ZaloConnectionId, accountId, cancellationToken);
        if (connection is null) return true;

        using var document = JsonDocument.Parse(protector.Unprotect(connection.EncryptedCredentials));
        var credentials = document.RootElement.Clone();
        var roles = await bridge.GetGroupRolesAsync(credentials, tracked.GroupId);
        var organizerIds = GetOrganizerIds(roles);
        if (!organizerIds.Contains(senderId, StringComparer.Ordinal))
        {
            // Non-organizers are bystanders for Auto Session. Do not create extra bot
            // chatter in a busy group merely because somebody replied to the preview.
            return stronglyAddressed;
        }

        var activeOrganizerStillAuthorized = organizerIds.Contains(
            NormalizeId(conversation.ActiveOrganizerId),
            StringComparer.Ordinal);
        var trustedFallbackId = NormalizeId(roles.CreatorId);
        var senderTrustedForTakeover = string.Equals(
            senderId,
            trustedFallbackId,
            StringComparison.Ordinal);
        var organizerRoute = ZaloAutoSessionOrganizerRouting.Evaluate(
            senderId,
            NormalizeId(conversation.ActiveOrganizerId),
            activeOrganizerStillAuthorized,
            senderTrustedForTakeover,
            stronglyAddressed,
            conversation.ReminderCount >= 2,
            incoming.Content);

        if (organizerRoute == ZaloAutoSessionOrganizerRoute.IgnoreBystander)
            return stronglyAddressed;

        if (organizerRoute == ZaloAutoSessionOrganizerRoute.RejectEarlyTakeover)
        {
            await SendConversationTextAsync(
                conversation,
                incoming.SenderId,
                incoming.SenderName,
                "Poll này đang có một trưởng/phó xử lý. Tui chưa chuyển quyền hội thoại để tránh hai người sửa chồng nhau. Nếu người đó im lặng, bot sẽ tự escalation; lúc đó bạn có thể reply “nhận xử lý”.",
                cancellationToken);
            return true;
        }

        var draft = DeserializeDraft(conversation.DraftJson);
        var stateBefore = conversation.State;
        var interpretation = await interpreter.InterpretAsync(
            incoming.Content,
            draft,
            conversation.State,
            conversation.LastQuestionType,
            cancellationToken);

        await conversations.AddTurnAsync(
            conversation.Id,
            messageId,
            "Organizer",
            senderId,
            incoming.SenderName,
            incoming.Content,
            interpretation.Intent.ToString(),
            interpretation.Interpreter,
            interpretation.Confidence,
            cancellationToken);

        conversation.ActiveOrganizerId = senderId;
        conversation.LastOrganizerMessageAt = DateTimeOffset.UtcNow;
        conversation.LastIntent = interpretation.Intent.ToString();
        // A real organizer response restarts the silence clock. This prevents an old
        // reminder from causing an immediate takeover escalation after the organizer
        // has already resumed the conversation.
        conversation.ReminderCount = 0;
        conversation.NextFollowUpAt = DateTimeOffset.UtcNow.AddMinutes(GetFirstReminderMinutes());
        conversation.LastError = null;
        conversation.Version += 1;

        if (organizerRoute == ZaloAutoSessionOrganizerRoute.AllowTakeover &&
            ZaloAutoSessionOrganizerRouting.IsExplicitTakeover(incoming.Content) &&
            interpretation.Intent is ZaloAutoSessionConversationIntent.None or ZaloAutoSessionConversationIntent.Uncertain)
        {
            conversation.State = ZaloAutoSessionConversationState.Discussing;
            conversation.LastQuestionType = null;
            await conversations.SaveAsync(conversation, cancellationToken);
            await SendConversationTextAsync(
                conversation,
                incoming.SenderId,
                incoming.SenderName,
                BuildDraftSummary(
                    draft,
                    tracked,
                    "Ok, từ giờ tui giữ poll này cho bạn xử lý. Bản nháp hiện tại như dưới đây."),
                cancellationToken);
            return true;
        }

        if (interpretation.Intent == ZaloAutoSessionConversationIntent.Cancel &&
            !string.Equals(interpretation.Interpreter, "rules", StringComparison.Ordinal))
        {
            conversation.State = ZaloAutoSessionConversationState.Clarifying;
            conversation.LastQuestionType = "cancel";
            await conversations.SaveAsync(conversation, cancellationToken);
            await SendConversationTextAsync(
                conversation,
                incoming.SenderId,
                incoming.SenderName,
                "Tui hiểu bạn có vẻ muốn dừng poll này, nhưng để tránh AI hiểu nhầm tui chưa đóng. Nếu muốn bỏ thật, nói rõ “bỏ qua” hoặc “không tạo”.",
                cancellationToken);
            return true;
        }

        if (interpretation.Intent == ZaloAutoSessionConversationIntent.Cancel)
        {
            conversation.State = ZaloAutoSessionConversationState.Cancelled;
            conversation.NextFollowUpAt = null;
            conversation.LastQuestionType = null;
            await conversations.SaveAsync(conversation, cancellationToken);

            var proposal = await autoSessions.GetProposalAsync(tracked.Id, conversation.PollId, cancellationToken);
            if (proposal is not null)
            {
                proposal.Status = ZaloPollSessionProposalStatus.Rejected;
                proposal.ApprovedByZaloUserId = senderId;
                proposal.ApprovedAt = DateTimeOffset.UtcNow;
                proposal.LastError = "cancelled_by_organizer_conversation_v3";
                await autoSessions.UpsertProposalAsync(proposal, cancellationToken);
            }

            await SendConversationTextAsync(
                conversation,
                incoming.SenderId,
                incoming.SenderName,
                "Ok, tui dừng poll này. Website chưa tạo gì từ poll này.",
                cancellationToken);
            return true;
        }

        if (interpretation.Intent == ZaloAutoSessionConversationIntent.Reset)
        {
            draft = DeserializeDraft(conversation.InitialDraftJson);
            conversation.DraftJson = JsonSerializer.Serialize(draft, JsonOptions);
            conversation.State = ZaloAutoSessionConversationState.Discussing;
            conversation.LastQuestionType = null;
            await conversations.SaveAsync(conversation, cancellationToken);
            await SendConversationTextAsync(
                conversation,
                incoming.SenderId,
                incoming.SenderName,
                BuildDraftSummary(draft, tracked, "Tui đã đưa bản nháp về đúng thông tin ban đầu của poll."),
                cancellationToken);
            return true;
        }

        if (interpretation.NeedsClarification)
        {
            conversation.State = ZaloAutoSessionConversationState.Clarifying;
            conversation.LastQuestionType = interpretation.QuestionType;
            await conversations.SaveAsync(conversation, cancellationToken);
            await SendConversationTextAsync(
                conversation,
                incoming.SenderId,
                incoming.SenderName,
                interpretation.Clarification ??
                "Tui chưa đủ chắc để đổi bản nháp. Bạn nói rõ ngày/giờ muốn sửa giúp tui; website vẫn chưa được tạo.",
                cancellationToken);
            return true;
        }

        if (string.Equals(interpretation.Location, "__INITIAL__", StringComparison.Ordinal))
        {
            var initial = DeserializeDraft(conversation.InitialDraftJson);
            interpretation = interpretation with { Location = initial.Location };
        }

        var changed = ApplyInterpretation(ref draft, interpretation);
        if (changed)
        {
            conversation.DraftJson = JsonSerializer.Serialize(draft, JsonOptions);
            conversation.LastQuestionType = null;
        }

        if (draft.Items.All(item => !item.Selected))
        {
            conversation.State = ZaloAutoSessionConversationState.Clarifying;
            conversation.LastQuestionType = "selection";
            await conversations.SaveAsync(conversation, cancellationToken);
            await SendConversationTextAsync(
                conversation,
                incoming.SenderId,
                incoming.SenderName,
                "Hiện bạn đã bỏ hết lịch trong bản nháp. Bạn muốn giữ ngày nào? Ví dụ “T6 thôi” hoặc “T6 CN”. Website vẫn chưa được tạo.",
                cancellationToken);
            return true;
        }

        var ruleConfirmed = interpretation.Intent == ZaloAutoSessionConversationIntent.Confirm &&
                            string.Equals(interpretation.Interpreter, "rules", StringComparison.Ordinal);

        if (stronglyAddressed &&
            (interpretation.ExplicitExecute ||
             (ruleConfirmed && stateBefore == ZaloAutoSessionConversationState.ReadyToConfirm)))
        {
            conversation.State = ZaloAutoSessionConversationState.ReadyToConfirm;
            await conversations.SaveAsync(conversation, cancellationToken);
            return await ExecuteAsync(conversation.Id, senderId, incoming.SenderName, accountId, cancellationToken);
        }

        if (implicitContext && interpretation.Intent == ZaloAutoSessionConversationIntent.Confirm)
        {
            conversation.State = ZaloAutoSessionConversationState.ReadyToConfirm;
            await conversations.SaveAsync(conversation, cancellationToken);
            await SendConversationTextAsync(
                conversation,
                incoming.SenderId,
                incoming.SenderName,
                BuildDraftSummary(
                    draft,
                    tracked,
                    "Tui bắt được ý của bạn từ đoạn chat ngay sau preview, nhưng để tránh hiểu nhầm lời nói chuyện trong group, bước tạo cuối cần reply tin bot này hoặc @bot rồi nói “tạo đi”."), 
                cancellationToken);
            return true;
        }

        if (interpretation.Intent == ZaloAutoSessionConversationIntent.Confirm)
        {
            conversation.State = ZaloAutoSessionConversationState.ReadyToConfirm;
            await conversations.SaveAsync(conversation, cancellationToken);
            await SendConversationTextAsync(
                conversation,
                incoming.SenderId,
                incoming.SenderName,
                BuildDraftSummary(
                    draft,
                    tracked,
                    "Tui hiểu bạn có vẻ đồng ý, nhưng câu này chưa đủ chắc để tự tạo. Nếu đúng bản nháp dưới đây, nói rõ “tạo đi”."),
                cancellationToken);
            return true;
        }

        if (changed || interpretation.Intent == ZaloAutoSessionConversationIntent.ModifyDraft)
        {
            conversation.State = ZaloAutoSessionConversationState.ReadyToConfirm;
            await conversations.SaveAsync(conversation, cancellationToken);
            await SendConversationTextAsync(
                conversation,
                incoming.SenderId,
                incoming.SenderName,
                BuildDraftSummary(draft, tracked, "Tui cập nhật bản nháp như này."),
                cancellationToken);
            return true;
        }

        conversation.State = ZaloAutoSessionConversationState.Discussing;
        await conversations.SaveAsync(conversation, cancellationToken);
        await SendConversationTextAsync(
            conversation,
            incoming.SenderId,
            incoming.SenderName,
            "Tui chưa bắt được ý cần đổi. Bạn cứ nói tự nhiên kiểu “T6 thôi”, “à thêm CN”, “T6 6h”, “sân A”, hoặc “tạo đi”. Website vẫn chưa được tạo.",
            cancellationToken);
        return true;
    }

    private async Task EnsurePendingConversationsAsync(CancellationToken cancellationToken)
    {
        await conversations.EnsureAsync(cancellationToken);
        await autoSessions.EnsureAsync(cancellationToken);
        var eligible = await conversations.GetConversationEligibleProposalKeysAsync(cancellationToken);

        foreach (var key in eligible)
        {
            var proposal = await autoSessions.GetProposalAsync(key.TrackedGroupId, key.PollId, cancellationToken);
            if (proposal is null) continue;
            if (string.IsNullOrWhiteSpace(proposal.ProposalMessageId)) continue;
            if (await conversations.GetByProposalAsync(proposal.Id, cancellationToken) is not null) continue;

            var tracked = await autoSessions.GetTrackedGroupAsync(proposal.TrackedGroupId, cancellationToken);
            if (tracked is null || !tracked.AutoSessionEnabled) continue;
            var candidates = DeserializeCandidates(proposal.CandidatesJson);
            if (candidates.Count == 0) continue;

            await conversations.CreateFromPreviewAsync(
                proposal,
                tracked,
                candidates,
                proposal.ProposalMessageId.Trim(),
                configuration,
                cancellationToken);
        }
    }

    private async Task ProcessConversationHistoryAsync(CancellationToken cancellationToken)
    {
        var active = await conversations.GetActiveAsync(cancellationToken);
        foreach (var group in active.GroupBy(item => new { item.TrackedGroupId, item.GroupId }))
        {
            var tracked = await autoSessions.GetTrackedGroupAsync(group.Key.TrackedGroupId, cancellationToken);
            if (tracked is null || !tracked.AutoSessionEnabled) continue;

            var connection = await db.ZaloConnections
                .AsNoTracking()
                .SingleOrDefaultAsync(item =>
                    item.Id == tracked.ZaloConnectionId &&
                    item.Status == ZaloConnectionStatus.Connected,
                    cancellationToken);
            if (connection is null) continue;

            try
            {
                using var document = JsonDocument.Parse(protector.Unprotect(connection.EncryptedCredentials));
                var history = await bridge.GetGroupMessageHistoryAsync(
                    document.RootElement.Clone(),
                    tracked.GroupId,
                    Math.Clamp(configuration.GetValue("AutoSession:ConversationHistoryCount", 200), 50, 500),
                    cancellationToken);
                if (!history.IsSupported) continue;

                var oldestConversationAt = group.Min(item => item.CreatedAt).AddMinutes(-1).ToUnixTimeMilliseconds();
                foreach (var message in history.Messages
                             .Where(item => !item.IsFromBot && item.SentAtUnixMs >= oldestConversationAt)
                             .OrderBy(item => item.SentAtUnixMs))
                {
                    var incoming = new ZaloIncomingMessageEvent(
                        connection.AccountZaloId,
                        string.Empty,
                        tracked.GroupId,
                        message.MessageId,
                        message.SenderId,
                        message.SenderName,
                        message.Content,
                        [],
                        false,
                        message.SentAtUnixMs,
                        message.Quote is null
                            ? null
                            : new ZaloBridgeMessageQuote(
                                message.Quote.MessageId,
                                message.Quote.SenderId,
                                message.Quote.SenderName,
                                message.Quote.Content,
                                message.Quote.MessageType,
                                message.Quote.SentAtUnixMs,
                                message.Quote.Attachment));
                    await TryHandleIncomingAsync(incoming, cancellationToken);
                }
            }
            catch (Exception exception) when (exception is not OperationCanceledException)
            {
                logger.LogDebug(exception, "Auto Session V3 history scan failed Group={GroupId}", tracked.GroupId);
            }
        }
    }

    private async Task ProcessFollowUpsAsync(CancellationToken cancellationToken)
    {
        var now = DateTimeOffset.UtcNow;
        foreach (var conversation in await conversations.GetDueAsync(now, cancellationToken))
        {
            if (conversation.ExpiresAt <= now)
            {
                await ExpireAsync(conversation, "conversation_v3_expired", cancellationToken);
                continue;
            }

            if (conversation.ReminderCount >= 2)
            {
                conversation.NextFollowUpAt = null;
                await conversations.SaveAsync(conversation, cancellationToken);
                continue;
            }

            var tracked = await autoSessions.GetTrackedGroupAsync(conversation.TrackedGroupId, cancellationToken);
            if (tracked is null || !tracked.AutoSessionEnabled)
            {
                conversation.State = ZaloAutoSessionConversationState.Superseded;
                conversation.NextFollowUpAt = null;
                await conversations.SaveAsync(conversation, cancellationToken);
                continue;
            }

            var connection = await db.ZaloConnections
                .AsNoTracking()
                .SingleOrDefaultAsync(item =>
                    item.Id == tracked.ZaloConnectionId &&
                    item.Status == ZaloConnectionStatus.Connected,
                    cancellationToken);
            if (connection is null) continue;

            try
            {
                using var document = JsonDocument.Parse(protector.Unprotect(connection.EncryptedCredentials));
                var credentials = document.RootElement.Clone();
                var roles = await bridge.GetGroupRolesAsync(credentials, tracked.GroupId);
                var organizers = GetOrganizerIds(roles);
                if (organizers.Count == 0) continue;
                var trustedFallbackId = NormalizeId(roles.CreatorId);

                IReadOnlyList<string> targets;
                string text;
                if (conversation.ReminderCount == 0 &&
                    organizers.Contains(conversation.ActiveOrganizerId, StringComparer.Ordinal))
                {
                    targets = [conversation.ActiveOrganizerId];
                    text =
                        "Nhắc nhẹ: preview lịch này vẫn đang chờ bạn xử lý và website CHƯA được tạo. " +
                        "Bạn cứ bấm Trả lời tin này rồi nói tự nhiên như “T6 thôi”, “T6 6h”, hoặc “tạo đi”.";
                    conversation.ReminderCount = 1;
                    conversation.NextFollowUpAt = now.AddMinutes(GetEscalationDelayMinutes());
                }
                else
                {
                    // Interim trusted-operator policy: Zalo admin alone is not enough to
                    // receive/take over Auto Session. Until the explicit Trusted Backup UI
                    // exists, only the current Zalo group creator is a fallback operator.
                    var fallbackAvailable =
                        trustedFallbackId.Length > 0 &&
                        organizers.Contains(trustedFallbackId, StringComparer.Ordinal) &&
                        !string.Equals(
                            trustedFallbackId,
                            NormalizeId(conversation.ActiveOrganizerId),
                            StringComparison.Ordinal);

                    conversation.ReminderCount = 2;
                    conversation.NextFollowUpAt = null;
                    if (!fallbackAvailable)
                    {
                        await conversations.SaveAsync(conversation, cancellationToken);
                        continue;
                    }

                    targets = [trustedFallbackId];
                    text =
                        "Poll này vẫn chưa được xử lý nên website CHƯA được tạo. " +
                        "Bạn là trưởng nhóm fallback cho Auto Session. Nếu muốn xử lý thay, hãy bấm Trả lời tin này rồi nói “nhận xử lý” hoặc nói rõ lịch cần chỉnh. " +
                        "Bot vẫn sẽ chốt lại trước khi tạo website.";
                }

                await SendConversationTextAsync(
                    conversation,
                    targets,
                    credentials,
                    connection,
                    text,
                    cancellationToken);
            }
            catch (Exception exception) when (exception is not OperationCanceledException)
            {
                logger.LogDebug(exception, "Auto Session V3 follow-up failed Conversation={ConversationId}", conversation.Id);
            }
        }
    }

    private async Task<bool> ExecuteAsync(
        string conversationId,
        string organizerId,
        string organizerName,
        string accountId,
        CancellationToken cancellationToken)
    {
        var conversation = await conversations.GetByIdAsync(conversationId, cancellationToken);
        if (conversation is null) return true;
        if (conversation.State != ZaloAutoSessionConversationState.ReadyToConfirm) return true;

        var runtime = await runtimeStore.GetRuntimeAsync(cancellationToken);
        var tracked = await autoSessions.GetTrackedGroupAsync(conversation.TrackedGroupId, cancellationToken);
        if (!runtime.GlobalEnabled || tracked is null || !tracked.AutoSessionEnabled)
        {
            conversation.State = ZaloAutoSessionConversationState.Superseded;
            conversation.NextFollowUpAt = null;
            conversation.LastError = "auto_session_disabled";
            conversation.Version += 1;
            await conversations.SaveAsync(conversation, cancellationToken);
            return true;
        }

        var rollout = await runtimeStore.GetRolloutModeAsync(conversation.TrackedGroupId, cancellationToken);
        if (rollout == ZaloAutoSessionRolloutMode.PreviewOnly)
        {
            conversation.State = ZaloAutoSessionConversationState.ReadyToConfirm;
            conversation.LastError = "preview_only_no_write";
            conversation.Version += 1;
            await conversations.SaveAsync(conversation, cancellationToken);
            await SendConversationTextAsync(
                conversation,
                organizerId,
                organizerName,
                "Group này đang ở PreviewOnly nên tui đã hiểu/xác nhận được ý bạn nhưng sẽ KHÔNG tạo website. Khi admin chuyển sang Live, poll mới sẽ được phép đi tới bước tạo.",
                cancellationToken);
            return true;
        }

        if (rollout != ZaloAutoSessionRolloutMode.Live)
        {
            conversation.State = ZaloAutoSessionConversationState.Superseded;
            conversation.NextFollowUpAt = null;
            conversation.LastError = "auto_session_rollout_disabled";
            conversation.Version += 1;
            await conversations.SaveAsync(conversation, cancellationToken);
            return true;
        }

        var connection = await GetConnectionAsync(tracked.ZaloConnectionId, accountId, cancellationToken);
        if (connection is null) return true;

        using var document = JsonDocument.Parse(protector.Unprotect(connection.EncryptedCredentials));
        var credentials = document.RootElement.Clone();
        var roles = await bridge.GetGroupRolesAsync(credentials, tracked.GroupId);
        var organizers = GetOrganizerIds(roles);
        if (!organizers.Contains(organizerId, StringComparer.Ordinal))
        {
            await SendConversationTextAsync(
                conversation,
                organizerId,
                organizerName,
                "Quyền trưởng/phó của bạn đã thay đổi nên tui chưa thể tạo lịch. Website vẫn chưa được tạo.",
                cancellationToken);
            return true;
        }

        var proposal = await autoSessions.GetProposalAsync(tracked.Id, conversation.PollId, cancellationToken);
        if (proposal is null)
        {
            conversation.State = ZaloAutoSessionConversationState.Failed;
            conversation.LastError = "proposal_missing";
            conversation.Version += 1;
            await conversations.SaveAsync(conversation, cancellationToken);
            return true;
        }

        var currentPoll = await bridge.GetPollAsync(credentials, conversation.PollId);
        if (!string.Equals(
                ZaloPollScheduleParser.ComputeStructureHash(currentPoll),
                proposal.PollStructureHash,
                StringComparison.Ordinal))
        {
            conversation.State = ZaloAutoSessionConversationState.Superseded;
            conversation.NextFollowUpAt = null;
            conversation.LastError = "poll_structure_changed_before_v3_confirmation";
            conversation.Version += 1;
            await conversations.SaveAsync(conversation, cancellationToken);

            proposal.Status = ZaloPollSessionProposalStatus.Superseded;
            proposal.LastError = conversation.LastError;
            await autoSessions.UpsertProposalAsync(proposal, cancellationToken);

            await SendConversationTextAsync(
                conversation,
                organizerId,
                organizerName,
                "Poll hiện không còn giống bản preview ban đầu nên tui không tạo để tránh nhầm. Hãy để bot đọc poll mới lại.",
                cancellationToken);
            return true;
        }

        var draft = DeserializeDraft(conversation.DraftJson);
        var selected = draft.Items
            .Where(item => item.Selected)
            .Select(item =>
            {
                var latest = currentPoll.Options.FirstOrDefault(option =>
                    string.Equals(option.Id, item.OptionId, StringComparison.Ordinal));
                return new ZaloAutoSessionCandidate(
                    item.OptionId,
                    item.OptionContent,
                    item.DayKey,
                    item.StartTime,
                    latest?.VoteCount ?? item.VoteCount);
            })
            .ToList();
        if (selected.Count == 0) return true;

        if (!await conversations.TryClaimExecutionAsync(conversation.Id, conversation.Version, cancellationToken))
        {
            await SendConversationTextAsync(
                conversation,
                organizerId,
                organizerName,
                "Yêu cầu này vừa được một trưởng/phó khác xử lý hoặc trạng thái đã thay đổi. Tui không chạy lần hai.",
                cancellationToken);
            return true;
        }

        try
        {
            var executor = ZaloAutoSessionActionExecutor.Create(serviceProvider);
            await executor.ExecuteAsync(
                tracked,
                connection,
                currentPoll,
                proposal,
                selected,
                organizers,
                organizerId,
                draft.Location,
                draft.TeamSize,
                cancellationToken);

            conversation = await conversations.GetByIdAsync(conversation.Id, cancellationToken) ?? conversation;
            conversation.State = ZaloAutoSessionConversationState.Created;
            conversation.NextFollowUpAt = null;
            conversation.LastError = null;
            conversation.Version += 1;
            await conversations.SaveAsync(conversation, cancellationToken);
            return true;
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            logger.LogWarning(exception, "Auto Session V3 execution failed Conversation={ConversationId}", conversation.Id);
            conversation = await conversations.GetByIdAsync(conversation.Id, cancellationToken) ?? conversation;
            var refreshedProposal = await autoSessions.GetProposalAsync(
                conversation.TrackedGroupId,
                conversation.PollId,
                cancellationToken);
            if (refreshedProposal?.Status == ZaloPollSessionProposalStatus.Created)
            {
                // The transaction may already have committed and only a post-create Zalo
                // notification/sync step failed. Never reopen the final confirmation gate.
                conversation.State = ZaloAutoSessionConversationState.Created;
                conversation.NextFollowUpAt = null;
                conversation.LastError = Truncate(exception.Message, 1000);
                conversation.Version += 1;
                await conversations.SaveAsync(conversation, cancellationToken);
                return true;
            }

            conversation.State = ZaloAutoSessionConversationState.ReadyToConfirm;
            conversation.LastError = Truncate(exception.Message, 1000);
            conversation.Version += 1;
            await conversations.SaveAsync(conversation, cancellationToken);
            await SendConversationTextAsync(
                conversation,
                organizerId,
                organizerName,
                "Tui chưa tạo được website vì có lỗi kỹ thuật. Bản nháp vẫn được giữ và chưa chạy lại tự động.",
                cancellationToken);
            return true;
        }
    }

    private static bool ApplyInterpretation(
        ref ZaloAutoSessionConversationDraft draft,
        ZaloAutoSessionConversationInterpretation interpretation)
    {
        var changed = false;
        var days = interpretation.Days.ToHashSet(StringComparer.OrdinalIgnoreCase);
        var items = draft.Items.Select(item =>
        {
            var selected = item.Selected;
            if (days.Count > 0)
            {
                selected = interpretation.SelectionMode switch
                {
                    ZaloAutoSessionSelectionMode.Replace => days.Contains(item.DayKey),
                    ZaloAutoSessionSelectionMode.Add => item.Selected || days.Contains(item.DayKey),
                    ZaloAutoSessionSelectionMode.Remove => item.Selected && !days.Contains(item.DayKey),
                    _ => item.Selected
                };
            }

            var startTime = item.StartTime;
            if (interpretation.TimeOverrides.TryGetValue(item.DayKey, out var minutes))
            {
                minutes = Math.Clamp(minutes, 0, 1439);
                var local = item.StartTime.ToOffset(TimeSpan.FromHours(7));
                startTime = new DateTimeOffset(local.Date.AddMinutes(minutes), TimeSpan.FromHours(7));
            }

            changed |= selected != item.Selected || startTime != item.StartTime;
            return item with { Selected = selected, StartTime = startTime };
        }).ToList();

        var location = draft.Location;
        if (!string.IsNullOrWhiteSpace(interpretation.Location) &&
            !string.Equals(location, interpretation.Location.Trim(), StringComparison.Ordinal))
        {
            location = interpretation.Location.Trim();
            changed = true;
        }

        var teamSize = draft.TeamSize;
        if (interpretation.TeamSize is { } requestedTeamSize)
        {
            requestedTeamSize = Math.Clamp(requestedTeamSize, 2, 30);
            if (requestedTeamSize != teamSize)
            {
                teamSize = requestedTeamSize;
                changed = true;
            }
        }

        draft = new ZaloAutoSessionConversationDraft(items, location, teamSize);
        return changed;
    }

    private async Task ExpireAsync(
        ZaloAutoSessionConversationData conversation,
        string reason,
        CancellationToken cancellationToken)
    {
        conversation.State = ZaloAutoSessionConversationState.Expired;
        conversation.NextFollowUpAt = null;
        conversation.LastError = reason;
        conversation.Version += 1;
        await conversations.SaveAsync(conversation, cancellationToken);

        var tracked = await autoSessions.GetTrackedGroupAsync(conversation.TrackedGroupId, cancellationToken);
        if (tracked is null) return;
        var proposal = await autoSessions.GetProposalAsync(tracked.Id, conversation.PollId, cancellationToken);
        if (proposal is null || proposal.Status != ZaloPollSessionProposalStatus.AwaitingApproval) return;
        proposal.Status = ZaloPollSessionProposalStatus.Superseded;
        proposal.LastError = reason;
        await autoSessions.UpsertProposalAsync(proposal, cancellationToken);
    }

    private async Task SendAmbiguousConversationMessageAsync(
        ZaloAutoSessionConversationData fallback,
        ZaloIncomingMessageEvent incoming,
        CancellationToken cancellationToken)
    {
        var tracked = await autoSessions.GetTrackedGroupAsync(fallback.TrackedGroupId, cancellationToken);
        if (tracked is null) return;
        var connection = await GetConnectionAsync(tracked.ZaloConnectionId, NormalizeId(incoming.AccountId), cancellationToken);
        if (connection is null) return;
        await SendConversationTextAsync(
            fallback,
            incoming.SenderId,
            incoming.SenderName,
            "Nhóm đang có nhiều poll chờ xử lý. Để tránh tạo nhầm, bạn reply trực tiếp vào preview/reminder của poll muốn xử lý nhé.",
            cancellationToken);
    }

    private async Task SendConversationTextAsync(
        ZaloAutoSessionConversationData conversation,
        string targetId,
        string targetName,
        string text,
        CancellationToken cancellationToken)
    {
        var tracked = await autoSessions.GetTrackedGroupAsync(conversation.TrackedGroupId, cancellationToken);
        if (tracked is null) return;
        var connection = await db.ZaloConnections
            .AsNoTracking()
            .SingleOrDefaultAsync(item =>
                item.Id == tracked.ZaloConnectionId &&
                item.Status == ZaloConnectionStatus.Connected,
                cancellationToken);
        if (connection is null) return;

        using var document = JsonDocument.Parse(protector.Unprotect(connection.EncryptedCredentials));
        await SendConversationTextAsync(
            conversation,
            [NormalizeId(targetId)],
            document.RootElement.Clone(),
            connection,
            text,
            cancellationToken,
            new Dictionary<string, string>(StringComparer.Ordinal)
            {
                [NormalizeId(targetId)] = targetName
            });
    }

    private async Task SendConversationTextAsync(
        ZaloAutoSessionConversationData conversation,
        IReadOnlyList<string> targetIds,
        JsonElement credentials,
        ZaloConnection connection,
        string text,
        CancellationToken cancellationToken,
        IReadOnlyDictionary<string, string>? knownNames = null)
    {
        var names = knownNames ?? await ResolveNamesAsync(credentials, targetIds);
        var outgoing = BuildMentionMessage(targetIds, names, text);
        var sent = await bridge.SendGroupMessageAsync(
            connection.AccountZaloId,
            conversation.GroupId,
            outgoing.Message,
            outgoing.Mentions,
            idempotencyKey: $"auto-session-v3:{conversation.Id}:{conversation.Version}:{conversation.ReminderCount}:{conversation.State}");
        if (!sent.Sent || string.IsNullOrWhiteSpace(sent.MessageId)) return;

        conversation.CurrentBotMessageId = sent.MessageId.Trim();
        conversation.LastBotMessageAt = DateTimeOffset.UtcNow;
        await conversations.SaveAsync(conversation, cancellationToken);
        await conversations.AddTurnAsync(
            conversation.Id,
            sent.MessageId.Trim(),
            "Bot",
            connection.AccountZaloId,
            connection.DisplayName,
            text,
            conversation.State.ToString(),
            "system",
            1,
            cancellationToken);
    }

    private static string BuildDraftSummary(
        ZaloAutoSessionConversationDraft draft,
        ZaloTrackedGroupData tracked,
        string intro)
    {
        var capacity = Math.Max(1, tracked.DefaultTeamCount) * Math.Max(2, draft.TeamSize);
        var selected = draft.Items.Where(item => item.Selected).OrderBy(item => item.StartTime).ToList();
        var lines = selected.Select(item =>
            $"• {item.DayKey} {item.StartTime.ToOffset(TimeSpan.FromHours(7)):dd/MM HH:mm} — hiện {item.VoteCount}/{capacity} người");
        var location = string.IsNullOrWhiteSpace(draft.Location) ? "chưa chốt" : draft.Location.Trim();

        return $"{intro}\n\n" +
               $"{string.Join("\n", lines)}\n" +
               $"• Địa điểm: {location}\n\n" +
               "Website CHƯA được tạo.\n" +
               "Nếu đúng, bấm Trả lời tin bot này rồi nói “tạo đi”. Muốn sửa thì cứ nói tiếp tự nhiên.";
    }

    private async Task<ZaloConnection?> GetConnectionAsync(
        string connectionId,
        string accountId,
        CancellationToken cancellationToken) =>
        await db.ZaloConnections
            .AsNoTracking()
            .SingleOrDefaultAsync(item =>
                item.Id == connectionId &&
                item.Status == ZaloConnectionStatus.Connected &&
                item.AccountZaloId == accountId,
                cancellationToken);

    private async Task<IReadOnlyDictionary<string, string>> ResolveNamesAsync(
        JsonElement credentials,
        IReadOnlyList<string> ids)
    {
        try
        {
            var members = await bridge.GetMembersAsync(credentials, ids);
            return members
                .GroupBy(item => NormalizeId(item.ZaloUserId), StringComparer.Ordinal)
                .ToDictionary(
                    group => group.Key,
                    group => group.First().DisplayName,
                    StringComparer.Ordinal);
        }
        catch (Exception exception) when (exception is HttpRequestException or TaskCanceledException)
        {
            logger.LogDebug(exception, "Could not resolve Auto Session V3 organizer names");
            return ids.ToDictionary(id => id, id => id, StringComparer.Ordinal);
        }
    }

    private static IReadOnlyList<string> GetOrganizerIds(BridgeGroupRoles roles) =>
        new[] { NormalizeId(roles.CreatorId) }
            .Concat(roles.AdminIds.Select(NormalizeId))
            .Where(id => id.Length > 0)
            .Distinct(StringComparer.Ordinal)
            .ToList();

    private static (string Message, IReadOnlyList<BridgeOutgoingMention> Mentions) BuildMentionMessage(
        IReadOnlyList<string> targetIds,
        IReadOnlyDictionary<string, string> names,
        string body)
    {
        var builder = new StringBuilder();
        var mentions = new List<BridgeOutgoingMention>();
        foreach (var id in targetIds.Distinct(StringComparer.Ordinal))
        {
            var name = names.GetValueOrDefault(id, id).Trim();
            if (name.Length == 0) name = id;
            var token = $"@{name}";
            if (builder.Length > 0) builder.Append(' ');
            var pos = builder.Length;
            builder.Append(token);
            mentions.Add(new BridgeOutgoingMention(id, pos, token.Length));
        }
        if (builder.Length > 0) builder.Append('\n');
        builder.Append(body);
        return (builder.ToString(), mentions);
    }

    private static ZaloAutoSessionConversationDraft DeserializeDraft(string json)
    {
        try
        {
            return JsonSerializer.Deserialize<ZaloAutoSessionConversationDraft>(json, JsonOptions)
                   ?? new ZaloAutoSessionConversationDraft([], null, 6);
        }
        catch (JsonException)
        {
            return new ZaloAutoSessionConversationDraft([], null, 6);
        }
    }

    private static IReadOnlyList<ZaloAutoSessionCandidate> DeserializeCandidates(string json)
    {
        try
        {
            return JsonSerializer.Deserialize<List<ZaloAutoSessionCandidate>>(json, JsonOptions) ?? [];
        }
        catch (JsonException)
        {
            return [];
        }
    }

    private int GetFirstReminderMinutes() =>
        Math.Clamp(configuration.GetValue("AutoSession:ConversationFirstReminderMinutes", 30), 5, 360);

    private int GetEscalationDelayMinutes() =>
        Math.Clamp(configuration.GetValue("AutoSession:ConversationEscalationDelayMinutes", 150), 15, 720);

    private int GetExpiryHours() =>
        Math.Clamp(configuration.GetValue("AutoSession:ConversationExpiryHours", 24), 3, 72);

    private static bool LooksLikeImplicitConversationReply(string? content)
    {
        var normalized = ZaloPollScheduleParser.NormalizeText(content);
        if (normalized.Length == 0 || normalized.Length > 80) return false;
        if (Regex.IsMatch(
                normalized,
                @"(?<![a-z0-9])(dong qua|it qua|ai danh|ai di|ai choi|haha|hehe|kkk)(?![a-z0-9])",
                RegexOptions.CultureInvariant))
            return false;
        return Regex.IsMatch(
            normalized,
            @"(?<![a-z0-9])((?:t|thu)\s*[2-7]|cn|chu\s*nhat|\d{1,2}\s*(?:h|:)|tao|lam|chot|trien|bo|khoi|them|san|ok|oke|u|uh|dung roi)(?![a-z0-9])",
            RegexOptions.CultureInvariant);
    }

    private static bool IsImplicitFollowUpWindow(
        ZaloAutoSessionConversationData conversation,
        string senderId)
    {
        if (!string.Equals(conversation.ActiveOrganizerId, senderId, StringComparison.Ordinal) &&
            !string.Equals(conversation.OriginalOrganizerId, senderId, StringComparison.Ordinal))
            return false;
        if (conversation.LastBotMessageAt is null) return false;
        return DateTimeOffset.UtcNow - conversation.LastBotMessageAt <= TimeSpan.FromMinutes(3);
    }

    private static string NormalizeId(string? value)
    {
        var normalized = value?.Trim() ?? string.Empty;
        return normalized.EndsWith("_0", StringComparison.Ordinal) ? normalized[..^2] : normalized;
    }

    private static string? Truncate(string? value, int maxLength)
    {
        var text = value?.Trim();
        if (string.IsNullOrWhiteSpace(text)) return null;
        return text.Length <= maxLength ? text : text[..maxLength];
    }
}
