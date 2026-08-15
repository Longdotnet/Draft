using Microsoft.Extensions.Configuration;
using VolleyDraft.Api.Contracts;
using VolleyDraft.Api.Data;

namespace VolleyDraft.Api.Services;

internal sealed record OverbookOutgoingMessage(string Message, IReadOnlyList<BridgeOutgoingMention> Mentions);

internal sealed record OverbookObservation(
    VolleyDraft.Api.Models.MatchSession Session,
    IReadOnlyList<string> SelectedOptionIds,
    IReadOnlyList<string> OrderedVoterIds,
    IReadOnlyDictionary<string, string> DisplayNames,
    IReadOnlyDictionary<string, string> SharedSlotByVoter,
    OverbookCapacityEvaluation Capacity,
    BridgePoll Poll);

public sealed partial class ZaloOverbookService
{
    private readonly VolleyDraftDbContext db;
    private readonly ZaloBridgeClient bridge;
    private readonly ZaloCredentialProtector protector;
    private readonly ZaloIntegrationService integration;
    private readonly AiAssistantService ai;
    private readonly IConfiguration configuration;
    private readonly ILogger<ZaloOverbookService> logger;
    private readonly ZaloBotService? botService;
    private readonly ZaloOverbookStateStore store;

    public ZaloOverbookService(
        VolleyDraftDbContext db,
        ZaloBridgeClient bridge,
        ZaloCredentialProtector protector,
        ZaloIntegrationService integration,
        AiAssistantService ai,
        IConfiguration configuration,
        ILogger<ZaloOverbookService> logger,
        ZaloBotService? botService = null)
    {
        this.db = db;
        this.bridge = bridge;
        this.protector = protector;
        this.integration = integration;
        this.ai = ai;
        this.configuration = configuration;
        this.logger = logger;
        this.botService = botService;
        store = new ZaloOverbookStateStore(db);
    }

    private static readonly IReadOnlyList<string> DefaultFriendlyMessages =
    [
        "Kèo {sessionName} đang dư {excessCount} slot ({effectiveSlotCount}/{capacity}). Các lượt vote cuối kiểm tra và bỏ vote dư giúp để nhóm còn chốt draft nha.",
        "{sessionName} chỉ có {capacity} slot mà hiện đang {effectiveSlotCount}. Mấy bạn vote vượt slot kiểm tra lại giúp nhóm nha."
    ];
    private static readonly IReadOnlyList<string> DefaultSeriousMessages =
    [
        "{sessionName} vẫn đang vượt {excessCount} slot ({effectiveSlotCount}/{capacity}). Các lượt vote dư vui lòng xử lý để hệ thống có thể chốt đủ slot và draft.",
        "Nhắc lần {reminderNumber}: {sessionName} hiện vẫn {effectiveSlotCount}/{capacity}. Các lượt vote vượt giới hạn vui lòng bỏ vote dư giúp cả nhóm."
    ];
    private static readonly IReadOnlyList<string> DefaultStrictMessages =
    [
        "Nhắc lần {reminderNumber}: {sessionName} vẫn {effectiveSlotCount}/{capacity}. Các lượt vote dư xử lý ngay giúp, hiện cả nhóm chưa thể chốt roster để draft.",
        "{sessionName} đã được nhắc nhiều lần nhưng vẫn dư {excessCount} slot. Những lượt vote vượt giới hạn vui lòng bỏ vote để không làm kẹt việc draft của cả nhóm."
    ];

    public async Task<ServiceResult<ZaloOverbookStatusResponse>> GetStatusAsync(
        string adminUserId,
        string sessionId,
        CancellationToken cancellationToken = default)
    {
        var owned = await GetOwnedSessionAsync(adminUserId, sessionId, cancellationToken);
        if (owned is null)
            return ServiceResult<ZaloOverbookStatusResponse>.Failure(StatusCodes.Status404NotFound, "Không tìm thấy session.");

        try
        {
            return ServiceResult<ZaloOverbookStatusResponse>.Success(
                await ObserveAsync(sessionId, null, cancellationToken));
        }
        catch (InvalidOperationException exception)
        {
            var state = await GetOrCreateStateAsync(sessionId, cancellationToken);
            state.LastError = exception.Message;
            await store.SaveAsync(state, cancellationToken);
            return ServiceResult<ZaloOverbookStatusResponse>.Success(
                await BuildStatusFromStoredStateAsync(owned, state, cancellationToken));
        }
        catch (Exception exception)
        {
            logger.LogWarning(exception, "Could not read overbook status Session={SessionId}", sessionId);
            return ServiceResult<ZaloOverbookStatusResponse>.Failure(
                StatusCodes.Status502BadGateway,
                $"Không thể đọc trạng thái vượt slot: {exception.Message}");
        }
    }

    public async Task<ServiceResult<ZaloOverbookStatusResponse>> UpdateSettingsAsync(
        string adminUserId,
        string sessionId,
        UpdateZaloOverbookSettingsRequest request,
        CancellationToken cancellationToken = default)
    {
        var owned = await GetOwnedSessionAsync(adminUserId, sessionId, cancellationToken);
        if (owned is null)
            return ServiceResult<ZaloOverbookStatusResponse>.Failure(StatusCodes.Status404NotFound, "Không tìm thấy session.");
        if (request.GraceMinutes is < 0 or > 1440)
            return ServiceResult<ZaloOverbookStatusResponse>.Failure(StatusCodes.Status400BadRequest, "Thời gian chờ lần đầu phải từ 0 đến 1440 phút.");
        if (request.ReminderIntervalMinutes is < 5 or > 10080)
            return ServiceResult<ZaloOverbookStatusResponse>.Failure(StatusCodes.Status400BadRequest, "Khoảng cách nhắc phải từ 5 phút đến 7 ngày.");
        if (request.MaxReminders is < 1 or > 100)
            return ServiceResult<ZaloOverbookStatusResponse>.Failure(StatusCodes.Status400BadRequest, "Số lần nhắc tối đa phải từ 1 đến 100.");

        var state = await GetOrCreateStateAsync(sessionId, cancellationToken);
        state.Enabled = request.Enabled;
        state.GraceMinutes = request.GraceMinutes;
        state.ReminderIntervalMinutes = request.ReminderIntervalMinutes;
        state.MaxReminders = request.MaxReminders;
        state.MessageSource = request.MessageSource;
        state.FriendlyMessages = NormalizeMessages(request.FriendlyMessages);
        state.SeriousMessages = NormalizeMessages(request.SeriousMessages);
        state.StrictMessages = NormalizeMessages(request.StrictMessages);
        if (request.ReminderMessageBanks is not null)
            state.ReminderMessageBanks = MergeExactReminderBanks(state.ReminderMessageBanks, request.ReminderMessageBanks);
        if (request.StageMessageBanks is not null)
            state.ReminderMessageBanks = MergeStageBanks(state.ReminderMessageBanks, request.StageMessageBanks);
        if (!state.Enabled) state.NextReminderAt = null;
        await store.SaveAsync(state, cancellationToken);

        try
        {
            return ServiceResult<ZaloOverbookStatusResponse>.Success(
                await ObserveAsync(sessionId, null, cancellationToken));
        }
        catch (InvalidOperationException exception)
        {
            state.LastError = exception.Message;
            await store.SaveAsync(state, cancellationToken);
            return ServiceResult<ZaloOverbookStatusResponse>.Success(
                await BuildStatusFromStoredStateAsync(owned, state, cancellationToken));
        }
    }

    public async Task<ServiceResult<ZaloOverbookStatusResponse>> ConfirmTargetsAsync(
        string adminUserId,
        string sessionId,
        ConfirmZaloOverbookTargetsRequest request,
        CancellationToken cancellationToken = default)
    {
        var owned = await GetOwnedSessionAsync(adminUserId, sessionId, cancellationToken);
        if (owned is null)
            return ServiceResult<ZaloOverbookStatusResponse>.Failure(StatusCodes.Status404NotFound, "Không tìm thấy session.");

        OverbookObservation observation;
        try
        {
            await ObserveAsync(sessionId, null, cancellationToken);
            observation = await ReadObservationAsync(sessionId, cancellationToken);
        }
        catch (Exception exception)
        {
            return ServiceResult<ZaloOverbookStatusResponse>.Failure(StatusCodes.Status502BadGateway, $"Không thể đồng bộ poll để xác nhận: {exception.Message}");
        }

        var normalized = (request.ZaloUserIds ?? [])
            .Select(ZaloOverbookLogic.NormalizeId)
            .Where(id => id.Length > 0)
            .Distinct(StringComparer.Ordinal)
            .ToList();
        var validation = ValidateConfirmedTargets(observation, normalized);
        if (validation is not null)
            return ServiceResult<ZaloOverbookStatusResponse>.Failure(StatusCodes.Status400BadRequest, validation);

        var state = await GetOrCreateStateAsync(sessionId, cancellationToken);
        ApplyConfirmedTargets(state, normalized, DateTimeOffset.UtcNow);
        await store.SaveAsync(state, cancellationToken);
        return ServiceResult<ZaloOverbookStatusResponse>.Success(
            await BuildStatusAsync(observation, state, cancellationToken));
    }

    public async Task<ZaloOverbookStatusResponse> ObserveAsync(
        string sessionId,
        string? actorId,
        CancellationToken cancellationToken = default)
    {
        var observation = await ReadObservationAsync(sessionId, cancellationToken);
        var now = DateTimeOffset.UtcNow;
        var state = await GetOrCreateStateAsync(sessionId, cancellationToken);
        var isFirstObservation = state.LastObservedVoterIds.Count == 0 && state.LastObservedAt is null;
        var previousTargets = state.CurrentTargetVoterIds.ToList();
        var previousObserved = state.LastObservedVoterIds.ToList();
        var previousEffective = state.EffectiveSlotCount;
        var currentIds = observation.OrderedVoterIds.ToList();
        var sourceIsSingleOption = observation.SelectedOptionIds.Count == 1;
        var trustedTransition = !isFirstObservation &&
                                sourceIsSingleOption &&
                                ZaloOverbookLogic.IsTrustedOrderTransition(
                                    previousObserved,
                                    currentIds,
                                    actorId);

        if (isFirstObservation)
            state.FirstObservedVoterIds = currentIds.ToList();

        state.CurrentPollId = observation.Poll.Id;
        state.CurrentSelectedOptionIds = observation.SelectedOptionIds.ToList();
        state.LastPollUpdatedAtUnixMs = observation.Poll.UpdatedAtUnixMs;
        state.LastObservedVoterIds = currentIds;
        state.RawVoterCount = currentIds.Count;
        state.EffectiveSlotCount = observation.Capacity.EffectiveSlotCount;
        state.ExcessSlotCount = observation.Capacity.ExcessSlotCount;
        state.SuggestedTargetVoterIds = observation.Capacity.SuggestedTargetIds.ToList();
        state.LastActorId = ZaloOverbookLogic.NormalizeId(actorId) is { Length: > 0 } normalizedActor ? normalizedActor : null;
        state.LastObservedAt = now;
        state.LastError = null;

        if (observation.Capacity.ExcessSlotCount <= 0)
        {
            ResolveIncident(state);
            state.OrderConfidence = sourceIsSingleOption ? "ObservedWithinCapacity" : "MultiOptionWithinCapacity";
        }
        else if (!observation.Capacity.CanResolveFromPoll)
        {
            RequireConfirmation(state, "NonPollCapacityConflict");
            state.LastError = "Roster đã vượt capacity bởi slot không đến từ poll; không thể tự quy trách nhiệm cho voter.";
        }
        else if (!sourceIsSingleOption)
        {
            if (state.OrderConfidence != "AdminConfirmed" ||
                !TargetsStillValid(observation, state.CurrentTargetVoterIds))
            {
                RequireConfirmation(state, "MultipleOptionsUncertain");
            }
        }
        else if (isFirstObservation && observation.Capacity.ExcessSlotCount > 0)
        {
            RequireConfirmation(state, "InitialSnapshotOverCapacity");
        }
        else if (state.OrderConfidence == "AdminConfirmed" &&
                 TargetsStillValid(observation, state.CurrentTargetVoterIds) &&
                 previousEffective == observation.Capacity.EffectiveSlotCount)
        {
            state.NeedsConfirmation = false;
        }
        else if ((state.OrderConfidence is "ObservedWithinCapacity" or "ObservedLive" or "AdminConfirmed") && trustedTransition ||
                 (state.OrderConfidence == "ObservedLive" &&
                  previousObserved.SequenceEqual(currentIds, StringComparer.Ordinal)))
        {
            StartOrUpdateObservedIncident(state, observation.Capacity.SuggestedTargetIds, now);
            state.OrderConfidence = "ObservedLive";
            state.NeedsConfirmation = false;
            state.ConfirmedTargetVoterIds = [];
        }
        else
        {
            RequireConfirmation(state, "OrderChangedUncertain");
        }

        if (!state.Enabled || state.NeedsConfirmation || state.ExcessSlotCount <= 0)
            state.NextReminderAt = null;
        else if (state.CurrentTargetVoterIds.Count > 0 && state.NextReminderAt is null && state.ReminderCount < state.MaxReminders)
            state.NextReminderAt = now.AddMinutes(state.GraceMinutes);

        if (!previousTargets.SequenceEqual(state.CurrentTargetVoterIds, StringComparer.Ordinal) &&
            state.CurrentTargetVoterIds.Count > 0 &&
            !state.NeedsConfirmation)
        {
            ResetReminderProgress(state, now);
        }

        await store.SaveAsync(state, cancellationToken);
        return await BuildStatusAsync(observation, state, cancellationToken);
    }

    public async Task<ServiceResult<int>> CopySettingsAsync(string adminUserId, string sessionId, CopyZaloOverbookSettingsRequest request, CancellationToken cancellationToken = default)
    {
        if (!string.Equals(sessionId, request.SourceSessionId, StringComparison.Ordinal))
            return ServiceResult<int>.Failure(StatusCodes.Status400BadRequest, "Source session không khớp route.");
        var sourceOwned = await GetOwnedSessionAsync(adminUserId, sessionId, cancellationToken);
        if (sourceOwned is null) return ServiceResult<int>.Failure(StatusCodes.Status404NotFound, "Không tìm thấy trận nguồn.");
        var source = await GetOrCreateStateAsync(sessionId, cancellationToken);
        var targets = request.TargetSessionIds.Where(id => !string.Equals(id, sessionId, StringComparison.Ordinal)).Distinct(StringComparer.Ordinal).Take(30).ToList();
        var copied = 0;
        foreach (var targetId in targets)
        {
            if (await GetOwnedSessionAsync(adminUserId, targetId, cancellationToken) is null) continue;
            var target = await GetOrCreateStateAsync(targetId, cancellationToken);
            if (request.CopyMessages)
            {
                target.FriendlyMessages = source.FriendlyMessages.ToList();
                target.SeriousMessages = source.SeriousMessages.ToList();
                target.StrictMessages = source.StrictMessages.ToList();
                target.ReminderMessageBanks = source.ReminderMessageBanks.ToDictionary(pair => pair.Key, pair => pair.Value.ToList());
            }
            if (request.CopyTiming) { target.GraceMinutes = source.GraceMinutes; target.ReminderIntervalMinutes = source.ReminderIntervalMinutes; }
            if (request.CopyMaxReminders) target.MaxReminders = source.MaxReminders;
            if (request.CopyMessageSource) target.MessageSource = source.MessageSource;
            // Runtime incident state is deliberately untouched.
            await store.SaveAsync(target, cancellationToken);
            copied++;
        }
        return ServiceResult<int>.Success(copied);
    }

    private static Dictionary<int, List<string>> MergeExactReminderBanks(
        IReadOnlyDictionary<int, List<string>> existing,
        IReadOnlyDictionary<int, IReadOnlyList<string>> banks)
    {
        var result = existing
            .Where(pair => pair.Key <= ZaloOverbookMessageCatalog.AdvancedExactStorageOffset ||
                           pair.Key > ZaloOverbookMessageCatalog.AdvancedExactStorageOffset + 100)
            .ToDictionary(pair => pair.Key, pair => pair.Value.ToList());
        foreach (var pair in banks.Where(pair => pair.Key is >= 1 and <= 100))
        {
            var normalized = NormalizeMessages(pair.Value, 20);
            if (normalized.Count > 0)
                result[ZaloOverbookMessageCatalog.GetAdvancedExactStorageKey(pair.Key)] = normalized;
        }
        return result;
    }

    private static Dictionary<int, List<string>> MergeStageBanks(
        IReadOnlyDictionary<int, List<string>> existing,
        IReadOnlyDictionary<string, IReadOnlyList<string>> banks)
    {
        var result = existing.ToDictionary(pair => pair.Key, pair => pair.Value.ToList());
        foreach (var pair in banks)
        {
            if (!ZaloOverbookMessageCatalog.TryGetStageStorageKey(pair.Key, out var storageKey)) continue;
            var normalized = NormalizeMessages(pair.Value, 200);
            if (normalized.Count > 0) result[storageKey] = normalized;
            else result.Remove(storageKey);
        }
        return result;
    }

}
