using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.EntityFrameworkCore;
using VolleyDraft.Api.Contracts;
using VolleyDraft.Api.Data;
using VolleyDraft.Api.Models;
using VolleyDraft.Api.Services.Zalo.Conversation;

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
    private static readonly TimeSpan VietnamOffset = TimeSpan.FromHours(7);

    internal enum ConversationShortcut
    {
        None,
        ShowOptions,
        CreateSelection
    }

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
    private readonly ZaloAutoSessionTrustedOrganizerStore trustedOrganizers = new(db);
    private readonly ZaloAutoSessionMatchProposalV4ReconciliationStore matchProposalReconciliation = new(db);

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
        var lateCreateRecovery = false;

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
                lateCreateRecovery = !implicitContext &&
                                     ZaloAutoSessionOrganizerRouting.IsSafeUnaddressedCreateRecovery(
                                         senderId,
                                         NormalizeId(conversation.ActiveOrganizerId),
                                         incoming.Content);
                if (!implicitContext && !lateCreateRecovery) return false;
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
        var trustedBackupIds = await trustedOrganizers.GetEnabledIdsAsync(tracked.Id, cancellationToken);
        var senderTrustedForTakeover =
            string.Equals(senderId, trustedFallbackId, StringComparison.Ordinal) ||
            trustedBackupIds.Contains(senderId);
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
        var shortcut = ParseConversationShortcut(incoming.Content);
        var selectionResolution = ResolveDraftSelection(incoming.Content, draft, DateTimeOffset.UtcNow);
        var selectorMutation = shortcut == ConversationShortcut.CreateSelection ||
                               ShouldApplySelectorFollowUp(incoming.Content, selectionResolution);
        var resolvedOptions = selectorMutation
            ? GetResolvedOptions(draft, selectionResolution)
            : [];
        var selectorChanged = false;

        if (selectorMutation && resolvedOptions.Count == 1)
        {
            var selectedDraft = SelectOnlyOption(draft, resolvedOptions[0].OptionId);
            selectorChanged = !HasSameSelection(draft, selectedDraft);
            draft = selectedDraft;
            if (selectorChanged)
                conversation.DraftJson = JsonSerializer.Serialize(draft, JsonOptions);
        }

        var interpretation = shortcut == ConversationShortcut.ShowOptions
            ? EmptyShortcutInterpretation()
            : selectorMutation && resolvedOptions.Count == 1
                ? InterpretSelectorMutation(
                    incoming.Content,
                    draft,
                    conversation.State,
                    conversation.LastQuestionType)
            : shortcut == ConversationShortcut.CreateSelection
                ? EmptyShortcutInterpretation() with
                {
                    Intent = ZaloAutoSessionConversationIntent.ModifyDraft,
                    Interpreter = "selector"
                }
                : await interpreter.InterpretAsync(
                    incoming.Content,
                    draft,
                    conversation.State,
                    conversation.LastQuestionType,
                    cancellationToken);
        var turnIntent = selectorMutation
            ? shortcut == ConversationShortcut.CreateSelection ? "CreateSelection" : "ModifySelection"
            : shortcut == ConversationShortcut.None
                ? interpretation.Intent.ToString()
                : shortcut.ToString();

        await conversations.AddTurnAsync(
            conversation.Id,
            messageId,
            "Organizer",
            senderId,
            incoming.SenderName,
            incoming.Content,
            turnIntent,
            interpretation.Interpreter,
            interpretation.Confidence,
            cancellationToken);

        conversation.ActiveOrganizerId = senderId;
        conversation.LastOrganizerMessageAt = DateTimeOffset.UtcNow;
        conversation.LastIntent = turnIntent;
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

        if (shortcut == ConversationShortcut.ShowOptions)
        {
            await conversations.SaveAsync(conversation, cancellationToken);
            await SendConversationTextAsync(
                conversation,
                incoming.SenderId,
                incoming.SenderName,
                BuildOptionSummary(draft, tracked),
                cancellationToken);
            return true;
        }

        if (selectorMutation && resolvedOptions.Count != 1)
        {
            conversation.State = ZaloAutoSessionConversationState.Clarifying;
            conversation.LastQuestionType = "selection";
            await conversations.SaveAsync(conversation, cancellationToken);
            await SendConversationTextAsync(
                conversation,
                incoming.SenderId,
                incoming.SenderName,
                BuildSelectionClarification(draft, resolvedOptions, selectionResolution),
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
            var sourceState = await matchProposalReconciliation.LoadSourceAsync(
                conversation.ProposalId,
                conversation.InitialDraftJson,
                cancellationToken);
            draft = sourceState?.Draft ?? DeserializeDraft(conversation.InitialDraftJson);
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

        var changed = ApplyInterpretation(ref draft, interpretation) || selectorChanged;
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

        if (lateCreateRecovery && interpretation.Intent == ZaloAutoSessionConversationIntent.Confirm)
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
                    "Tui thấy bạn muốn tạo lịch này. Vì “tạo đi” vừa được gửi như tin nhắn thường trong group nên tui chưa tạo ngay; tui kéo bản nháp lên lại để bạn xác nhận an toàn."),
                cancellationToken);
            return true;
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

    public async Task HandleCreateSelectionEntryAsync(
        ZaloIncomingMessageEvent incoming,
        CancellationToken cancellationToken = default)
    {
        var question = ZaloBotService.ExtractQuestion(incoming);
        if (!incoming.MentionedBot || !IsCreateSelectionCommand(question))
            return;

        var accountId = NormalizeId(incoming.AccountId);
        var groupId = NormalizeId(incoming.GroupId);
        var senderId = NormalizeId(incoming.SenderId);
        var messageId = NormalizeId(incoming.MessageId);
        if (accountId.Length == 0 || groupId.Length == 0 || senderId.Length == 0 || messageId.Length == 0)
            return;

        if (!configuration.GetValue("AutoSession:ConversationV3Enabled", true))
        {
            await SendCreateSelectionEntryMessageAsync(
                incoming,
                "Auto Session conversation đang tắt nên tui chưa mở bản nháp tạo trận từ lệnh này. Website chưa được tạo.",
                "conversation-disabled",
                cancellationToken);
            return;
        }

        var runtime = await runtimeStore.GetRuntimeAsync(cancellationToken);
        if (!runtime.GlobalEnabled)
        {
            await SendCreateSelectionEntryMessageAsync(
                incoming,
                "Auto Session đang tắt ở hệ thống nên tui chưa mở bản nháp tạo trận. Website chưa được tạo.",
                "runtime-disabled",
                cancellationToken);
            return;
        }

        var trackedGroups = await autoSessions.GetActiveTrackedGroupsForAccountAsync(
            accountId,
            groupId,
            cancellationToken);
        if (trackedGroups.Count == 0)
        {
            await SendCreateSelectionEntryMessageAsync(
                incoming,
                "Nhóm này chưa bật Auto Session nên tui chưa có poll nguồn để tạo trận. Website chưa được tạo.",
                "group-not-tracked",
                cancellationToken);
            return;
        }

        var matches = new List<CreateSelectionPollMatch>();
        var available = new List<CreateSelectionAvailableOption>();
        var hasLiveGroup = false;
        var senderAuthorized = false;
        var providerFailed = false;
        var scheduleConflict = false;
        var now = DateTimeOffset.UtcNow;
        var maxAgeDays = Math.Clamp(configuration.GetValue("AutoSession:PollMaxAgeDays", 21), 3, 90);
        var oldestPoll = now.AddDays(-maxAgeDays).ToUnixTimeMilliseconds();

        foreach (var tracked in trackedGroups)
        {
            if (await runtimeStore.GetRolloutModeAsync(tracked.Id, cancellationToken) != ZaloAutoSessionRolloutMode.Live)
                continue;
            hasLiveGroup = true;

            var connection = await GetConnectionAsync(tracked.ZaloConnectionId, accountId, cancellationToken);
            if (connection is null)
                continue;

            try
            {
                using var document = JsonDocument.Parse(protector.Unprotect(connection.EncryptedCredentials));
                var credentials = document.RootElement.Clone();
                var roles = await bridge.GetGroupRolesAsync(credentials, tracked.GroupId);
                var organizers = GetOrganizerIds(roles);
                if (!organizers.Contains(senderId, StringComparer.Ordinal))
                    continue;
                senderAuthorized = true;

                var learnedRules = await runtimeStore.GetApprovedDayTimeRulesAsync(tracked.Id, cancellationToken);
                var polls = await bridge.GetPollsAsync(credentials, tracked.GroupId);
                foreach (var poll in polls
                             .Where(item => !item.IsClosed && !item.IsAnonymous)
                             .Where(item => item.CreatedAtUnixMs <= 0 || item.CreatedAtUnixMs >= oldestPoll)
                             .OrderByDescending(item => item.UpdatedAtUnixMs))
                {
                    if (!organizers.Contains(NormalizeId(poll.CreatorId), StringComparer.Ordinal))
                        continue;

                    var extraction = ZaloPollScheduleParser.ExtractSchedule(poll, tracked, now);
                    if (extraction.Issues.Count > 0)
                    {
                        scheduleConflict = true;
                        continue;
                    }

                    var candidates = ZaloAutoSessionV2Service.ApplyLearnedDayDefaults(
                            extraction.Candidates,
                            learnedRules)
                        .Select(item => item with { StartTime = item.StartTime.ToUniversalTime() })
                        .ToList();
                    var usable = new List<ZaloAutoSessionCandidate>();
                    foreach (var candidate in candidates)
                    {
                        if (await autoSessions.GetLinkAsync(tracked.Id, poll.Id, candidate.OptionId, cancellationToken) is not null)
                            continue;
                        if (await HasMatchingSessionAsync(tracked, candidate, cancellationToken))
                            continue;
                        usable.Add(candidate);
                    }
                    if (usable.Count == 0)
                        continue;

                    available.AddRange(usable.Select(item => new CreateSelectionAvailableOption(
                        poll.Id,
                        item)));
                    var draft = BuildCreateSelectionDraft(tracked, poll.Question, usable);
                    var resolution = ResolveDraftSelection(question, draft, now);
                    var resolved = GetResolvedOptions(draft, resolution);
                    if (resolved.Count == 0)
                        continue;

                    matches.Add(new CreateSelectionPollMatch(
                        tracked,
                        poll,
                        usable,
                        resolved));
                }
            }
            catch (Exception exception) when (exception is HttpRequestException or TaskCanceledException or JsonException or InvalidOperationException)
            {
                providerFailed = true;
                logger.LogWarning(
                    exception,
                    "Could not resolve Auto Session create-selection entry Group={GroupId}",
                    tracked.GroupId);
            }
        }

        if (!hasLiveGroup)
        {
            await SendCreateSelectionEntryMessageAsync(
                incoming,
                "Auto Session của nhóm chưa ở chế độ Live nên tui chưa mở luồng tạo website từ lệnh này. Website chưa được tạo.",
                "rollout-not-live",
                cancellationToken);
            return;
        }

        if (!senderAuthorized)
        {
            await SendCreateSelectionEntryMessageAsync(
                incoming,
                providerFailed
                    ? "Tui chưa đọc được quyền/poll nguồn của nhóm lúc này nên chưa mở bản nháp tạo trận. Hãy thử lại sau một chút; website chưa được tạo."
                    : "Lệnh tạo trận từ poll này cần quyền trưởng/phó của nhóm. Website chưa được tạo.",
                providerFailed ? "provider-failed-before-auth" : "organizer-required",
                cancellationToken);
            return;
        }

        var distinctMatches = matches
            .GroupBy(item => item.Poll.Id, StringComparer.Ordinal)
            .Select(group => group.First())
            .ToList();
        if (distinctMatches.Count == 0)
        {
            var detail = providerFailed
                ? "Tui chưa đọc ổn định được poll nguồn của nhóm lúc này. Hãy thử lại sau một chút; website chưa được tạo."
                : BuildCreateSelectionNoMatchMessage(available, scheduleConflict);
            await SendCreateSelectionEntryMessageAsync(
                incoming,
                detail,
                providerFailed ? "provider-failed" : "no-match",
                cancellationToken);
            return;
        }

        if (distinctMatches.Count > 1)
        {
            await SendCreateSelectionEntryMessageAsync(
                incoming,
                BuildCreateSelectionCrossPollAmbiguity(distinctMatches),
                "multiple-polls",
                cancellationToken);
            return;
        }

        var match = distinctMatches[0];
        if (!await BootstrapCreateSelectionConversationAsync(match, incoming, cancellationToken))
            return;

        if (!await TryHandleIncomingAsync(incoming, cancellationToken))
        {
            await SendCreateSelectionEntryMessageAsync(
                incoming,
                "Tui đã đọc được poll nhưng chưa mở được bản nháp an toàn cho lệnh này. Website chưa được tạo; hãy thử lại hoặc reply trực tiếp preview Auto Session mới nhất.",
                "bootstrap-not-active",
                cancellationToken);
        }
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
                var trustedBackupIds = await trustedOrganizers.GetEnabledIdsAsync(tracked.Id, cancellationToken);
                var trustedFallbackTargets = new[] { trustedFallbackId }
                    .Concat(trustedBackupIds)
                    .Where(id => id.Length > 0)
                    .Where(id => organizers.Contains(id, StringComparer.Ordinal))
                    .Where(id => !string.Equals(id, NormalizeId(conversation.ActiveOrganizerId), StringComparison.Ordinal))
                    .Distinct(StringComparer.Ordinal)
                    .ToList();

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
                    var fallbackAvailable = trustedFallbackTargets.Count > 0;

                    conversation.ReminderCount = 2;
                    conversation.NextFollowUpAt = null;
                    if (!fallbackAvailable)
                    {
                        await conversations.SaveAsync(conversation, cancellationToken);
                        continue;
                    }

                    targets = trustedFallbackTargets;
                    text =
                        "Poll này vẫn chưa được xử lý nên website CHƯA được tạo. " +
                        "Bạn được cấu hình là người fallback đáng tin cậy cho Auto Session. Nếu muốn xử lý thay, hãy bấm Trả lời tin này rồi nói “nhận xử lý” hoặc nói rõ lịch cần chỉnh. " +
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

        var isOriginalOrganizer = string.Equals(
            organizerId,
            NormalizeId(conversation.OriginalOrganizerId),
            StringComparison.Ordinal);
        if (!isOriginalOrganizer)
        {
            var currentCreatorId = NormalizeId(roles.CreatorId);
            var trustedBackupIds = await trustedOrganizers.GetEnabledIdsAsync(tracked.Id, cancellationToken);
            var stillTrusted =
                string.Equals(organizerId, currentCreatorId, StringComparison.Ordinal) ||
                trustedBackupIds.Contains(organizerId);
            if (!stillTrusted)
            {
                await SendConversationTextAsync(
                    conversation,
                    organizerId,
                    organizerName,
                    "Quyền Auto Session operator của bạn đã thay đổi nên tui chưa tạo website. Bản nháp vẫn được giữ an toàn.",
                    cancellationToken);
                return true;
            }
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
        if (currentPoll.IsAnonymous || currentPoll.IsClosed)
        {
            conversation.State = ZaloAutoSessionConversationState.Clarifying;
            conversation.LastQuestionType = "poll_source";
            conversation.LastError = currentPoll.IsAnonymous
                ? "poll_became_anonymous_before_execution"
                : "poll_closed_before_execution";
            conversation.Version += 1;
            await conversations.SaveAsync(conversation, cancellationToken);
            await SendConversationTextAsync(
                conversation,
                organizerId,
                organizerName,
                currentPoll.IsAnonymous
                    ? "Poll hiện đang ẩn danh nên tui không thể xác minh roster authoritative để tạo lịch an toàn. Hãy dùng poll không ẩn danh; website vẫn chưa được tạo."
                    : "Poll đã đóng trước lúc tạo lịch nên tui dừng ở bước xác nhận thay vì dùng snapshot cũ. Nếu vẫn muốn tạo lịch, hãy mở/đăng poll authoritative mới rồi xác nhận lại.",
                cancellationToken);
            return true;
        }

        var durableDraft = DeserializeDraft(conversation.DraftJson);
        var sourceState = await matchProposalReconciliation.LoadSourceAsync(
            conversation.ProposalId,
            conversation.InitialDraftJson,
            cancellationToken);
        var sourceDraft = sourceState?.Draft ?? DeserializeDraft(conversation.InitialDraftJson);
        var revalidation = ZaloAutoSessionPollRevalidationWorkflowV4.Evaluate(
            currentPoll,
            tracked,
            sourceDraft,
            durableDraft);

        if (revalidation.Issues.Count > 0)
        {
            conversation.State = ZaloAutoSessionConversationState.Clarifying;
            conversation.LastQuestionType = "poll_source";
            conversation.LastError = "poll_revalidation_ambiguous";
            conversation.Version += 1;
            await conversations.SaveAsync(conversation, cancellationToken);
            await SendConversationTextAsync(
                conversation,
                organizerId,
                organizerName,
                ZaloAutoSessionPollRevalidationWorkflowV4.BuildOrganizerMessage(revalidation),
                cancellationToken);
            return true;
        }

        var currentHash = revalidation.CurrentStructureHash;
        var sourceHash = sourceState?.StructureHash ?? proposal.PollStructureHash;
        var unmodeledStructureChange = !string.Equals(currentHash, sourceHash, StringComparison.Ordinal) &&
                                       !revalidation.Reconciliation.HasChanges;
        if (revalidation.Reconciliation.HasChanges || unmodeledStructureChange)
        {
            conversation.Version += 1;
            conversation.DraftJson = JsonSerializer.Serialize(revalidation.Reconciliation.Draft, JsonOptions);
            var persisted = await matchProposalReconciliation.AppendReconciliationAsync(
                conversation,
                currentPoll,
                sourceDraft,
                revalidation,
                organizerId,
                cancellationToken);
            if (persisted is null || !persisted.Accepted)
            {
                await SendConversationTextAsync(
                    conversation,
                    organizerId,
                    organizerName,
                    "Poll vừa được một trưởng/phó khác xử lý hoặc bản nháp đã đổi. Tui không ghi đè quyết định mới hơn; hãy kiểm tra lại tin bot mới nhất.",
                    cancellationToken);
                return true;
            }

            conversation.DraftJson = persisted.Revision.DraftJson;
            proposal.PollQuestion = currentPoll.Question;
            proposal.PollUpdatedAtUnixMs = currentPoll.UpdatedAtUnixMs;
            proposal.PollStructureHash = currentHash;
            proposal.LastError = null;
            await autoSessions.UpsertProposalAsync(proposal, cancellationToken);

            if (revalidation.RequiresOrganizerConfirmation || unmodeledStructureChange)
            {
                conversation.State = ZaloAutoSessionConversationState.ReadyToConfirm;
                conversation.LastQuestionType = "poll_changed";
                conversation.LastError = unmodeledStructureChange
                    ? "poll_source_changed_requires_confirmation"
                    : "poll_material_change_requires_confirmation";
                await conversations.SaveAsync(conversation, cancellationToken);

                var prefix = unmodeledStructureChange
                    ? "Poll đã đổi thông tin nguồn sau preview. Phần lịch vẫn khớp, nhưng tui chưa tự đoán ý nghĩa thay đổi ngoài lịch; hãy kiểm tra bản nháp mới rồi xác nhận lại."
                    : ZaloAutoSessionPollRevalidationWorkflowV4.BuildOrganizerMessage(revalidation);
                await SendConversationTextAsync(
                    conversation,
                    organizerId,
                    organizerName,
                    BuildDraftSummary(revalidation.Reconciliation.Draft, tracked, prefix),
                    cancellationToken);
                return true;
            }

            conversation.LastError = null;
            await conversations.SaveAsync(conversation, cancellationToken);
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

    internal static bool ApplyInterpretation(
        ref ZaloAutoSessionConversationDraft draft,
        ZaloAutoSessionConversationInterpretation interpretation)
    {
        var changed = false;
        var days = interpretation.Days.ToHashSet(StringComparer.OrdinalIgnoreCase);
        var sourceItems = draft.Items;
        var selectedOnlyTimeDays = interpretation.TimeOverrides.Keys
            .Where(day =>
                sourceItems.Count(item => string.Equals(item.DayKey, day, StringComparison.OrdinalIgnoreCase)) > 1 &&
                sourceItems.Count(item =>
                    item.Selected && string.Equals(item.DayKey, day, StringComparison.OrdinalIgnoreCase)) == 1)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        var items = sourceItems.Select(item =>
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
            if (interpretation.TimeOverrides.TryGetValue(item.DayKey, out var minutes) &&
                (!selectedOnlyTimeDays.Contains(item.DayKey) || item.Selected))
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

    internal static ConversationShortcut ParseConversationShortcut(string? content)
    {
        var normalized = ZaloPollScheduleParser.NormalizeText(content);
        if (normalized.Length == 0 || normalized.Length > 160)
            return ConversationShortcut.None;

        if (IsShowOptionsCommand(normalized))
            return ConversationShortcut.ShowOptions;

        if (IsCreateSelectionCommand(content))
            return ConversationShortcut.CreateSelection;

        return ConversationShortcut.None;
    }

    internal static bool IsShowOptionsCommand(string? content)
    {
        var normalized = ZaloPollScheduleParser.NormalizeText(content);
        if (normalized.Length == 0 || normalized.Length > 100 ||
            !Regex.IsMatch(normalized, @"(?<![a-z0-9])options?(?![a-z0-9])", RegexOptions.CultureInvariant))
            return false;

        return normalized is "option" or "options" ||
               Regex.IsMatch(
                   normalized,
                   @"(?<![a-z0-9])(xem|lay|liet ke|cho (?:tui|toi|minh) xem)(?![a-z0-9])",
                   RegexOptions.CultureInvariant) ||
               normalized.Contains("hien tai", StringComparison.Ordinal) ||
               normalized.Contains("dang co", StringComparison.Ordinal);
    }

    internal static bool IsCreateSelectionCommand(string? content)
    {
        var normalized = ZaloPollScheduleParser.NormalizeText(content);
        if (normalized.Length == 0 || normalized.Length > 160)
            return false;
        if (Regex.IsMatch(
                normalized,
                @"(?<![a-z0-9])(khong tao|dung tao|khoi tao)(?![a-z0-9])",
                RegexOptions.CultureInvariant))
            return false;

        return Regex.IsMatch(
                   normalized,
                   @"(?<![a-z0-9])tao\s+(?:tran|lich|website)(?![a-z0-9])",
                   RegexOptions.CultureInvariant) &&
               ZaloSessionResolver.LooksLikeSelector(content ?? string.Empty);
    }

    internal static ZaloSessionResolution ResolveDraftSelection(
        string? content,
        ZaloAutoSessionConversationDraft draft,
        DateTimeOffset now)
    {
        if (string.IsNullOrWhiteSpace(content))
            return new([], "no_selector", false, false);

        var candidates = draft.Items
            .Select(item => new ZaloSessionReference(item.OptionId, item.OptionContent, item.StartTime))
            .ToList();
        return ZaloSessionResolver.Resolve(content, candidates, now);
    }

    internal static bool ShouldApplySelectorFollowUp(
        string? content,
        ZaloSessionResolution resolution)
    {
        if (string.IsNullOrWhiteSpace(content) || !resolution.HasExplicitSelector)
            return false;
        if (ZaloSessionResolver.LooksLikeStandaloneSelector(content))
            return true;

        var normalized = ZaloPollScheduleParser.NormalizeText(content);
        return Regex.IsMatch(
                   normalized,
                   @"(?<![a-z0-9])(?:doi|chuyen)\s+(?:(?:sang|qua)\s+)?",
                   RegexOptions.CultureInvariant) ||
               Regex.IsMatch(
                   normalized,
                   @"(?<![a-z0-9])(?:chon|lay|chi)(?![a-z0-9])",
                   RegexOptions.CultureInvariant) ||
               Regex.IsMatch(
                   normalized,
                   @"(?<![a-z0-9])(?:thoi|nha|nhe)\s*$",
                   RegexOptions.CultureInvariant);
    }

    internal static IReadOnlyList<ZaloAutoSessionConversationDraftItem> GetResolvedOptions(
        ZaloAutoSessionConversationDraft draft,
        ZaloSessionResolution resolution)
    {
        if (resolution.CandidateIds.Count == 0) return [];
        var ids = resolution.CandidateIds.ToHashSet(StringComparer.Ordinal);
        return draft.Items
            .Where(item => ids.Contains(item.OptionId))
            .OrderBy(item => item.StartTime)
            .ToList();
    }

    internal static ZaloAutoSessionConversationInterpretation InterpretSelectorMutation(
        string content,
        ZaloAutoSessionConversationDraft draft,
        ZaloAutoSessionConversationState state,
        string? lastQuestionType) =>
        ZaloAutoSessionConversationInterpreter.InterpretByRules(
            content,
            draft,
            state,
            lastQuestionType) with
        {
            Intent = ZaloAutoSessionConversationIntent.ModifyDraft,
            Days = [],
            SelectionMode = ZaloAutoSessionSelectionMode.None,
            ExplicitExecute = false,
            Interpreter = "selector"
        };

    internal static ZaloAutoSessionConversationDraft SelectOnlyOption(
        ZaloAutoSessionConversationDraft draft,
        string optionId) =>
        draft with
        {
            Items = draft.Items
                .Select(item => item with
                {
                    Selected = string.Equals(item.OptionId, optionId, StringComparison.Ordinal)
                })
                .ToList()
        };

    private static bool HasSameSelection(
        ZaloAutoSessionConversationDraft left,
        ZaloAutoSessionConversationDraft right) =>
        left.Items.Count == right.Items.Count &&
        left.Items.Zip(right.Items).All(pair =>
            string.Equals(pair.First.OptionId, pair.Second.OptionId, StringComparison.Ordinal) &&
            pair.First.Selected == pair.Second.Selected);

    private static ZaloAutoSessionConversationInterpretation EmptyShortcutInterpretation() =>
        new(
            ZaloAutoSessionConversationIntent.None,
            [],
            ZaloAutoSessionSelectionMode.None,
            new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase),
            null,
            null,
            false,
            false,
            null,
            null,
            1,
            "shortcut");

    private static string BuildOptionSummary(
        ZaloAutoSessionConversationDraft draft,
        ZaloTrackedGroupData tracked)
    {
        var capacity = Math.Max(1, tracked.DefaultTeamCount) * Math.Max(2, draft.TeamSize);
        var lines = draft.Items
            .OrderBy(item => item.StartTime)
            .Select(item =>
                $"• {(item.Selected ? "✅" : "▫️")} {item.OptionContent} → {item.DayKey} {item.StartTime.ToOffset(VietnamOffset):dd/MM HH:mm} — hiện {item.VoteCount}/{capacity} người");

        return "Các option trong bản nháp Auto Session hiện tại:\n" +
               string.Join("\n", lines) +
               "\n\n✅ = đang được chọn. Đây là bản nháp đang chờ xử lý; website chưa được tạo.";
    }

    private static string BuildSelectionClarification(
        ZaloAutoSessionConversationDraft draft,
        IReadOnlyList<ZaloAutoSessionConversationDraftItem> options,
        ZaloSessionResolution resolution)
    {
        if (options.Count == 0)
        {
            var available = draft.Items
                .OrderBy(item => item.StartTime)
                .Select(item =>
                    $"• {item.OptionContent} → {item.DayKey} {item.StartTime.ToOffset(VietnamOffset):dd/MM HH:mm}");
            return "Tui không tìm thấy option nào trong poll hiện tại khớp ngày/lịch bạn vừa yêu cầu.\n\n" +
                   "Các option đang có:\n" + string.Join("\n", available) +
                   "\n\nWebsite chưa được tạo.";
        }

        var lines = options.Select(item =>
            $"• {item.OptionContent} → {item.DayKey} {item.StartTime.ToOffset(VietnamOffset):dd/MM HH:mm}");
        return $"Tui tìm thấy {options.Count} option cùng khớp ({resolution.Reason}) nên chưa tự chọn để tránh tạo nhầm:\n" +
               string.Join("\n", lines) +
               "\n\nHãy reply rõ ngày + giờ, ví dụ “09/10 18h”. Website chưa được tạo.";
    }

    private async Task<bool> BootstrapCreateSelectionConversationAsync(
        CreateSelectionPollMatch match,
        ZaloIncomingMessageEvent incoming,
        CancellationToken cancellationToken)
    {
        var now = DateTimeOffset.UtcNow;
        var currentHash = ZaloPollScheduleParser.ComputeStructureHash(match.Poll);
        var existingProposal = await autoSessions.GetProposalAsync(
            match.Tracked.Id,
            match.Poll.Id,
            cancellationToken);

        if (existingProposal?.Status == ZaloPollSessionProposalStatus.Rejected)
        {
            await SendCreateSelectionEntryMessageAsync(
                incoming,
                "Poll này đã được trưởng/phó dừng trước đó nên tui không tự mở lại từ một lệnh mới. Hãy dùng poll mới hoặc preview Auto Session mới. Website chưa được tạo.",
                "proposal-rejected",
                cancellationToken);
            return false;
        }

        if (existingProposal?.Status == ZaloPollSessionProposalStatus.Created)
        {
            await SendCreateSelectionEntryMessageAsync(
                incoming,
                "Poll này đã có dữ liệu Auto Session được tạo trước đó nên tui không mở thêm một bản nháp trùng. Hãy kiểm tra trận hiện có.",
                "proposal-created",
                cancellationToken);
            return false;
        }

        if (existingProposal?.Status == ZaloPollSessionProposalStatus.Approved)
        {
            await SendCreateSelectionEntryMessageAsync(
                incoming,
                "Poll này đang ở bước xử lý đã duyệt nên tui không mở thêm một luồng tạo song song. Hãy kiểm tra tin Auto Session mới nhất.",
                "proposal-approved",
                cancellationToken);
            return false;
        }

        var existingConversation = existingProposal is null
            ? null
            : await conversations.GetByProposalAsync(existingProposal.Id, cancellationToken);
        if (existingConversation is not null)
        {
            if (IsActiveConversationState(existingConversation.State))
                return true;

            if (existingConversation.State == ZaloAutoSessionConversationState.Cancelled)
            {
                await SendCreateSelectionEntryMessageAsync(
                    incoming,
                    "Bản nháp của poll này đã bị hủy trước đó nên tui không tự mở lại. Hãy dùng poll mới hoặc preview Auto Session mới. Website chưa được tạo.",
                    "conversation-cancelled",
                    cancellationToken);
                return false;
            }

            if (existingConversation.State is ZaloAutoSessionConversationState.Created or
                ZaloAutoSessionConversationState.HandedOff or
                ZaloAutoSessionConversationState.Executing)
            {
                await SendCreateSelectionEntryMessageAsync(
                    incoming,
                    "Poll này đã hoặc đang được xử lý nên tui không mở thêm một bản nháp tạo trận song song.",
                    "conversation-terminal",
                    cancellationToken);
                return false;
            }

            var sameSource = existingProposal is not null &&
                             string.Equals(existingProposal.PollStructureHash, currentHash, StringComparison.Ordinal) &&
                             HasSameCreateSelectionOptionIdentity(
                                 DeserializeDraft(existingConversation.InitialDraftJson),
                                 match.Candidates);
            if (!sameSource)
            {
                await SendCreateSelectionEntryMessageAsync(
                    incoming,
                    "Poll đã đổi cấu trúc sau bản nháp cũ nên tui chưa tự mở lại để tránh tạo sai lịch. Hãy đợi/khởi tạo preview Auto Session mới từ poll hiện tại. Website chưa được tạo.",
                    "source-changed",
                    cancellationToken);
                return false;
            }
        }

        var proposal = existingProposal ?? new ZaloPollSessionProposalData
        {
            Id = Guid.NewGuid().ToString("n"),
            TrackedGroupId = match.Tracked.Id,
            PollId = match.Poll.Id,
            CreatedAt = now
        };
        proposal.PollQuestion = match.Poll.Question;
        proposal.PollCreatorId = NormalizeId(match.Poll.CreatorId);
        proposal.PollUpdatedAtUnixMs = match.Poll.UpdatedAtUnixMs;
        proposal.PollStructureHash = currentHash;
        proposal.CandidatesJson = JsonSerializer.Serialize(match.Candidates, JsonOptions);
        proposal.ClassifierConfidence = 1;
        proposal.ClassifierReason = "addressed_create_selection";
        proposal.Status = ZaloPollSessionProposalStatus.AwaitingApproval;
        proposal.ProposalMessageId = null;
        proposal.ApprovedByZaloUserId = null;
        proposal.ApprovedAt = null;
        proposal.LastError = null;
        proposal = await autoSessions.UpsertProposalAsync(proposal, cancellationToken);

        var draft = BuildCreateSelectionDraft(match.Tracked, match.Poll.Question, match.Candidates);
        var draftJson = JsonSerializer.Serialize(draft, JsonOptions);
        var expiryHours = Math.Clamp(
            configuration.GetValue("AutoSession:ConversationExpiryHours", 24),
            3,
            72);
        var bootstrapMessageId = $"entry:{NormalizeId(incoming.MessageId)}";

        ZaloAutoSessionConversationData conversation;
        if (existingConversation is null)
        {
            conversation = await conversations.CreateIfMissingAsync(
                new ZaloAutoSessionConversationData
                {
                    ProposalId = proposal.Id,
                    TrackedGroupId = match.Tracked.Id,
                    PollId = match.Poll.Id,
                    GroupId = match.Tracked.GroupId,
                    OriginalOrganizerId = NormalizeId(match.Poll.CreatorId),
                    ActiveOrganizerId = NormalizeId(incoming.SenderId),
                    State = ZaloAutoSessionConversationState.PreviewSent,
                    InitialDraftJson = draftJson,
                    DraftJson = draftJson,
                    PreviewMessageId = bootstrapMessageId,
                    CurrentBotMessageId = bootstrapMessageId,
                    Version = 0,
                    ReminderCount = 0,
                    LastBotMessageAt = null,
                    NextFollowUpAt = now.AddMinutes(GetFirstReminderMinutes()),
                    ExpiresAt = now.AddHours(expiryHours),
                    CreatedAt = now,
                    UpdatedAt = now
                },
                cancellationToken);
        }
        else
        {
            existingConversation.ActiveOrganizerId = NormalizeId(incoming.SenderId);
            existingConversation.State = ZaloAutoSessionConversationState.PreviewSent;
            existingConversation.DraftJson = draftJson;
            existingConversation.CurrentBotMessageId = bootstrapMessageId;
            existingConversation.LastQuestionType = null;
            existingConversation.LastIntent = null;
            existingConversation.Version += 1;
            existingConversation.ReminderCount = 0;
            existingConversation.LastOrganizerMessageAt = null;
            existingConversation.LastBotMessageAt = null;
            existingConversation.NextFollowUpAt = now.AddMinutes(GetFirstReminderMinutes());
            existingConversation.ExpiresAt = now.AddHours(expiryHours);
            existingConversation.LastError = null;
            conversation = await conversations.SaveAsync(existingConversation, cancellationToken);
        }

        if (!IsActiveConversationState(conversation.State))
        {
            await SendCreateSelectionEntryMessageAsync(
                incoming,
                "Poll đã được đọc nhưng trạng thái bản nháp vừa thay đổi bởi luồng khác. Tui chưa tạo website; hãy kiểm tra tin Auto Session mới nhất rồi thử lại.",
                "bootstrap-race",
                cancellationToken);
            return false;
        }

        return true;
    }

    private async Task<bool> HasMatchingSessionAsync(
        ZaloTrackedGroupData tracked,
        ZaloAutoSessionCandidate candidate,
        CancellationToken cancellationToken)
    {
        var start = candidate.StartTime.AddMinutes(-75);
        var end = candidate.StartTime.AddMinutes(75);
        return await db.MatchSessions
            .AsNoTracking()
            .AnyAsync(session =>
                session.ZaloConnectionId == tracked.ZaloConnectionId &&
                session.ZaloGroupId == tracked.GroupId &&
                session.Status != SessionStatus.Cancelled &&
                session.StartTime != null &&
                session.StartTime >= start &&
                session.StartTime <= end,
                cancellationToken);
    }

    private async Task SendCreateSelectionEntryMessageAsync(
        ZaloIncomingMessageEvent incoming,
        string text,
        string reason,
        CancellationToken cancellationToken)
    {
        var accountId = NormalizeId(incoming.AccountId);
        var groupId = NormalizeId(incoming.GroupId);
        var senderId = NormalizeId(incoming.SenderId);
        var messageId = NormalizeId(incoming.MessageId);
        if (accountId.Length == 0 || groupId.Length == 0 || senderId.Length == 0 || messageId.Length == 0)
            return;

        var names = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            [senderId] = incoming.SenderName
        };
        var outgoing = BuildMentionMessage([senderId], names, text);
        try
        {
            await bridge.SendGroupMessageAsync(
                accountId,
                groupId,
                outgoing.Message,
                outgoing.Mentions,
                idempotencyKey: $"auto-session-entry:{groupId}:{messageId}:{reason}");
        }
        catch (Exception exception) when (exception is HttpRequestException or TaskCanceledException)
        {
            logger.LogWarning(
                exception,
                "Could not send Auto Session create-selection entry response Group={GroupId} Reason={Reason}",
                groupId,
                reason);
        }
    }

    private static ZaloAutoSessionConversationDraft BuildCreateSelectionDraft(
        ZaloTrackedGroupData tracked,
        string pollQuestion,
        IReadOnlyList<ZaloAutoSessionCandidate> candidates)
    {
        var capacity = ZaloAutoSessionCapacityPolicyV5.Resolve(pollQuestion);
        var teamSize = capacity.HasExplicitCapacity && capacity.IsValid
            ? capacity.TeamSize
            : Math.Max(2, tracked.DefaultTeamSize);
        return new ZaloAutoSessionConversationDraft(
            candidates.Select(item => new ZaloAutoSessionConversationDraftItem(
                item.OptionId,
                item.OptionContent,
                item.DayKey,
                item.StartTime,
                item.VoteCount,
                true)).ToList(),
            tracked.DefaultLocation,
            teamSize);
    }

    private static bool HasSameCreateSelectionOptionIdentity(
        ZaloAutoSessionConversationDraft draft,
        IReadOnlyList<ZaloAutoSessionCandidate> candidates)
    {
        var current = candidates.Select(item => item.OptionId).OrderBy(id => id, StringComparer.Ordinal).ToArray();
        var previous = draft.Items.Select(item => item.OptionId).OrderBy(id => id, StringComparer.Ordinal).ToArray();
        return current.SequenceEqual(previous, StringComparer.Ordinal);
    }

    private static bool IsActiveConversationState(ZaloAutoSessionConversationState state) =>
        state is ZaloAutoSessionConversationState.PreviewSent or
            ZaloAutoSessionConversationState.Discussing or
            ZaloAutoSessionConversationState.Clarifying or
            ZaloAutoSessionConversationState.ReadyToConfirm;

    private static string BuildCreateSelectionNoMatchMessage(
        IReadOnlyList<CreateSelectionAvailableOption> available,
        bool scheduleConflict)
    {
        if (available.Count == 0)
        {
            return scheduleConflict
                ? "Tui có thấy poll lịch nhưng một hoặc nhiều option đang mâu thuẫn ngày/giờ nên chưa thể dùng làm nguồn tạo trận an toàn. Hãy sửa poll rồi thử lại; website chưa được tạo."
                : "Tui chưa tìm thấy option chưa tạo nào trong poll đang mở khớp ngày/lịch bạn yêu cầu. Website chưa được tạo.";
        }

        var lines = available
            .GroupBy(item => $"{item.PollId}:{item.Candidate.OptionId}", StringComparer.Ordinal)
            .Select(group => group.First())
            .OrderBy(item => item.Candidate.StartTime)
            .Take(12)
            .Select(item =>
                $"• {item.Candidate.DayKey} {item.Candidate.StartTime.ToOffset(VietnamOffset):dd/MM HH:mm} — {item.Candidate.OptionContent}");
        return "Tui không tìm thấy option nào khớp đúng ngày/lịch bạn vừa yêu cầu. Các option chưa tạo đang có:\n" +
               string.Join("\n", lines) +
               "\n\nWebsite chưa được tạo.";
    }

    private static string BuildCreateSelectionCrossPollAmbiguity(
        IReadOnlyList<CreateSelectionPollMatch> matches)
    {
        var lines = matches
            .SelectMany(match => match.ResolvedOptions.Select(item =>
                $"• {item.DayKey} {item.StartTime.ToOffset(VietnamOffset):dd/MM HH:mm} — {item.OptionContent} (poll: {Truncate(match.Poll.Question, 70)})"))
            .Take(12);
        return "Tui thấy nhiều poll đang mở cùng khớp yêu cầu nên chưa chọn để tránh tạo nhầm:\n" +
               string.Join("\n", lines) +
               "\n\nHãy @Bott tạo trận lại kèm ngày + giờ thật rõ hoặc reply trực tiếp preview của poll muốn dùng. Website chưa được tạo.";
    }

    private sealed record CreateSelectionPollMatch(
        ZaloTrackedGroupData Tracked,
        BridgePoll Poll,
        IReadOnlyList<ZaloAutoSessionCandidate> Candidates,
        IReadOnlyList<ZaloAutoSessionConversationDraftItem> ResolvedOptions);

    private sealed record CreateSelectionAvailableOption(
        string PollId,
        ZaloAutoSessionCandidate Candidate);

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
