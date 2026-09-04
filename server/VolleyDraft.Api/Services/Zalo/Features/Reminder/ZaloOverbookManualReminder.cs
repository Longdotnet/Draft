using System.Security.Cryptography;
using System.Text;
using VolleyDraft.Api.Contracts;

namespace VolleyDraft.Api.Services;

public sealed partial class ZaloOverbookService
{
    public Task<ServiceResult<ZaloOverbookStatusResponse>> ConfirmTargetsScopedAsync(
        string adminUserId,
        string sessionId,
        ConfirmZaloOverbookTargetsRequest request,
        CancellationToken cancellationToken = default) =>
        ConfirmTargetsCoreAsync(adminUserId, sessionId, request, remindNow: false, cancellationToken);

    public Task<ServiceResult<ZaloOverbookStatusResponse>> ConfirmTargetsAndRemindNowAsync(
        string adminUserId,
        string sessionId,
        ConfirmZaloOverbookTargetsRequest request,
        CancellationToken cancellationToken = default) =>
        ConfirmTargetsCoreAsync(adminUserId, sessionId, request, remindNow: true, cancellationToken);

    private async Task<ServiceResult<ZaloOverbookStatusResponse>> ConfirmTargetsCoreAsync(
        string adminUserId,
        string sessionId,
        ConfirmZaloOverbookTargetsRequest request,
        bool remindNow,
        CancellationToken cancellationToken)
    {
        var owned = await GetOwnedSessionAsync(adminUserId, sessionId, cancellationToken);
        if (owned is null)
            return ServiceResult<ZaloOverbookStatusResponse>.Failure(StatusCodes.Status404NotFound, "Không tìm thấy session.");
        if (remindNow && !owned.BotEnabled)
            return ServiceResult<ZaloOverbookStatusResponse>.Failure(StatusCodes.Status400BadRequest, "Bot Zalo của trận đang tắt. Bật bot trước khi nhắc ngay.");

        var synced = await integration.SyncLatestPollAsync(adminUserId, sessionId);
        if (!synced.IsSuccess)
            return ServiceResult<ZaloOverbookStatusResponse>.Failure(
                synced.StatusCode,
                synced.Error ?? "Không đồng bộ được poll mới nhất trước khi xác nhận lượt vote dư.");

        OverbookObservation observation;
        try
        {
            await ObserveAsync(sessionId, null, cancellationToken);
            observation = await ReadObservationAsync(sessionId, cancellationToken);
        }
        catch (Exception exception)
        {
            return ServiceResult<ZaloOverbookStatusResponse>.Failure(
                StatusCodes.Status502BadGateway,
                $"Không thể đồng bộ poll để xác nhận: {exception.Message}");
        }

        if (!MatchesExpectedScope(
                observation.Poll.Id,
                observation.SelectedOptionIds,
                request.ExpectedPollId,
                request.ExpectedSelectedOptionIds))
        {
            return ServiceResult<ZaloOverbookStatusResponse>.Failure(
                StatusCodes.Status409Conflict,
                "Poll/option của trận đã thay đổi sau lúc bạn mở màn hình. Hãy đồng bộ trạng thái rồi chọn lại người vote dư để tránh mention nhầm trận.");
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
        if (remindNow && !state.Enabled)
            return ServiceResult<ZaloOverbookStatusResponse>.Failure(
                StatusCodes.Status400BadRequest,
                "Hãy bật và lưu cảnh báo vượt slot trước khi dùng “Xác nhận & nhắc ngay”, để các lần nhắc tiếp theo còn được theo dõi đúng lịch.");

        var now = DateTimeOffset.UtcNow;
        ApplyConfirmedTargets(state, normalized, now);
        state.IncidentKey = BuildAdminIncidentKey(
            observation.Poll.Id,
            observation.SelectedOptionIds,
            observation.Poll.UpdatedAtUnixMs,
            observation.Capacity.EffectiveSlotCount,
            normalized);
        await store.SaveAsync(state, cancellationToken);

        if (!remindNow)
            return ServiceResult<ZaloOverbookStatusResponse>.Success(
                await BuildStatusAsync(observation, state, cancellationToken));

        try
        {
            // Re-read immediately before send. This is intentionally separate from
            // the confirmation read so a vote removed during the click cannot be tagged.
            observation = await ReadObservationAsync(sessionId, cancellationToken);
            if (!MatchesExpectedScope(
                    observation.Poll.Id,
                    observation.SelectedOptionIds,
                    request.ExpectedPollId,
                    request.ExpectedSelectedOptionIds))
            {
                RequireConfirmation(state, "OrderChangedUncertain");
                await store.SaveAsync(state, cancellationToken);
                return ServiceResult<ZaloOverbookStatusResponse>.Failure(
                    StatusCodes.Status409Conflict,
                    "Poll/option vừa thay đổi trước lúc gửi. Bot đã dừng gửi để tránh mention nhầm người; hãy đồng bộ và xác nhận lại.");
            }
            var revalidation = ValidateConfirmedTargets(observation, normalized);
            if (revalidation is not null)
            {
                RequireConfirmation(state, "TargetsNoLongerPresent");
                await store.SaveAsync(state, cancellationToken);
                return ServiceResult<ZaloOverbookStatusResponse>.Failure(StatusCodes.Status409Conflict, revalidation);
            }

            var reminderNumber = state.ReminderCount + 1;
            var body = await BuildReminderBodyAsync(owned, state, observation, reminderNumber, cancellationToken);
            var outgoing = BuildMentionMessage(normalized, observation.DisplayNames, body);
            var idempotencyKey = $"overbook:{sessionId}:{state.IncidentKey}:{reminderNumber}";
            var send = await bridge.SendGroupMessageAsync(
                owned.ZaloConnection!.AccountZaloId,
                owned.ZaloGroupId!,
                outgoing.Message,
                outgoing.Mentions,
                idempotencyKey: idempotencyKey);
            if (!send.Sent)
                throw new InvalidOperationException("Zalo bridge did not confirm manual overbook reminder send.");

            state.ReminderCount = reminderNumber;
            state.LastReminderAt = now;
            state.NextReminderAt = reminderNumber >= state.MaxReminders
                ? null
                : now.AddMinutes(state.ReminderIntervalMinutes);
            state.LastError = null;
            await store.SaveAsync(state, cancellationToken);

            var providerMessageId = NormalizeProviderMessageId(send.MessageId);
            if (providerMessageId is not null)
                await SaveBotMessageAsync(owned, providerMessageId, outgoing.Message, now, cancellationToken);
            return ServiceResult<ZaloOverbookStatusResponse>.Success(
                await BuildStatusAsync(observation, state, cancellationToken));
        }
        catch (Exception exception)
        {
            state.LastError = Truncate(exception.Message, 1000);
            state.NextReminderAt = now.AddMinutes(Math.Clamp(configuration.GetValue("Scheduler:RetryMinutes", 10), 5, 60));
            await store.SaveAsync(state, cancellationToken);
            logger.LogWarning(exception, "Could not send manual overbook reminder Session={SessionId}", sessionId);
            return ServiceResult<ZaloOverbookStatusResponse>.Failure(
                StatusCodes.Status502BadGateway,
                $"Đã xác nhận target nhưng chưa gửi được Zalo: {exception.Message}. Worker sẽ thử lại theo lịch retry.");
        }
    }

    internal static bool MatchesExpectedScope(
        string currentPollId,
        IReadOnlyList<string> currentSelectedOptionIds,
        string? expectedPollId,
        IReadOnlyList<string>? expectedSelectedOptionIds)
    {
        if (string.IsNullOrWhiteSpace(expectedPollId) && expectedSelectedOptionIds is null)
            return true; // backwards compatibility for older clients.
        if (!string.Equals(currentPollId, expectedPollId, StringComparison.Ordinal)) return false;
        var current = currentSelectedOptionIds.Where(id => !string.IsNullOrWhiteSpace(id)).ToHashSet(StringComparer.Ordinal);
        var expected = (expectedSelectedOptionIds ?? []).Where(id => !string.IsNullOrWhiteSpace(id)).ToHashSet(StringComparer.Ordinal);
        return current.SetEquals(expected);
    }

    internal static string BuildAdminIncidentKey(
        string pollId,
        IReadOnlyList<string> selectedOptionIds,
        long? pollUpdatedAtUnixMs,
        int effectiveSlotCount,
        IReadOnlyList<string> targets)
    {
        var source = string.Join('|', new[]
        {
            pollId,
            string.Join(',', selectedOptionIds.OrderBy(id => id, StringComparer.Ordinal)),
            pollUpdatedAtUnixMs?.ToString() ?? "0",
            effectiveSlotCount.ToString(),
            string.Join(',', targets.OrderBy(id => id, StringComparer.Ordinal))
        });
        return "admin-" + Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(source))).ToLowerInvariant()[..24];
    }
}
