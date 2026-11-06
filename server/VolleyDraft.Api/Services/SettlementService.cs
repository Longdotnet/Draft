using System.Globalization;
using System.Text;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using VolleyDraft.Api.Contracts;
using VolleyDraft.Api.Data;
using VolleyDraft.Api.Models;

namespace VolleyDraft.Api.Services;

public sealed class SettlementService(
    VolleyDraftDbContext db,
    ZaloBridgeClient bridge,
    ILogger<SettlementService> logger)
{
    private const int MaxMessageLength = 4000;
    private const int MaxDelayMinutes = 43_200;
    private const int MaxSendAttempts = 3;

    public async Task<ServiceResult<SettlementResponse>> GetAsync(
        string adminUserId,
        string sessionId,
        CancellationToken cancellationToken = default)
    {
        var settlement = await LoadOwnedSettlementAsync(adminUserId, sessionId, cancellationToken);
        return settlement is null
            ? ServiceResult<SettlementResponse>.Failure(StatusCodes.Status404NotFound, "Chưa có sổ tiền cho trận này.")
            : ServiceResult<SettlementResponse>.Success(ToResponse(settlement));
    }

    public async Task<ServiceResult<SettlementResponse>> CreateAsync(
        string adminUserId,
        string sessionId,
        CreateSettlementRequest request,
        CancellationToken cancellationToken = default)
    {
        if (!IsValidAmount(request.AmountPerFullSlot))
            return InvalidAmount();

        var session = await db.MatchSessions
            .Include(item => item.Players)
                .ThenInclude(player => player.PlayerProfile)
            .SingleOrDefaultAsync(
                item => item.Id == sessionId && item.AdminUserId == adminUserId,
                cancellationToken);
        if (session is null)
            return ServiceResult<SettlementResponse>.Failure(StatusCodes.Status404NotFound, "Không tìm thấy session.");

        if (await db.MatchSettlements.AnyAsync(item => item.SessionId == sessionId, cancellationToken))
            return ServiceResult<SettlementResponse>.Failure(StatusCodes.Status409Conflict, "Trận này đã có sổ tiền.");

        var now = DateTimeOffset.UtcNow;
        var settlement = new MatchSettlement
        {
            SessionId = session.Id,
            Session = session,
            AmountPerFullSlot = request.AmountPerFullSlot,
            CreatedAt = now,
            UpdatedAt = now
        };

        foreach (var player in session.Players.Where(item => item.IsPresent).OrderBy(item => item.DisplayName))
        {
            settlement.Entries.Add(new SettlementEntry
            {
                SettlementId = settlement.Id,
                SessionPlayerId = player.Id,
                SessionPlayer = player,
                DisplayNameSnapshot = player.DisplayName.Trim(),
                ZaloUserIdSnapshot = CleanZaloUserId(player.PlayerProfile?.ZaloUserId),
                SlotFactor = player.IsInsideSharedSlot ? 0.5m : 1m,
                PaymentStatus = SettlementPaymentStatus.Unpaid,
                CreatedAt = now,
                UpdatedAt = now
            });
        }

        db.MatchSettlements.Add(settlement);
        try
        {
            await db.SaveChangesAsync(cancellationToken);
        }
        catch (DbUpdateException)
        {
            if (await db.MatchSettlements.AsNoTracking()
                .AnyAsync(item => item.SessionId == sessionId, cancellationToken))
            {
                return ServiceResult<SettlementResponse>.Failure(StatusCodes.Status409Conflict, "Trận này đã có sổ tiền.");
            }
            throw;
        }

        await db.Entry(settlement).Collection(item => item.Messages).LoadAsync(cancellationToken);
        return ServiceResult<SettlementResponse>.Created(ToResponse(settlement));
    }

    public async Task<ServiceResult<SettlementResponse>> UpdateAsync(
        string adminUserId,
        string sessionId,
        UpdateSettlementRequest request,
        CancellationToken cancellationToken = default)
    {
        if (!IsValidAmount(request.AmountPerFullSlot))
            return InvalidAmount();

        var settlement = await LoadOwnedSettlementAsync(adminUserId, sessionId, cancellationToken, tracking: true);
        if (settlement is null)
            return ServiceResult<SettlementResponse>.Failure(StatusCodes.Status404NotFound, "Chưa có sổ tiền cho trận này.");
        if (settlement.IsClosed)
            return ServiceResult<SettlementResponse>.Failure(StatusCodes.Status409Conflict, "Sổ tiền đang đóng. Mở lại trước khi sửa.");

        settlement.AmountPerFullSlot = request.AmountPerFullSlot;
        settlement.UpdatedAt = DateTimeOffset.UtcNow;
        await db.SaveChangesAsync(cancellationToken);
        return ServiceResult<SettlementResponse>.Success(ToResponse(settlement));
    }

    public async Task<ServiceResult<SettlementResponse>> UpdateEntryAsync(
        string adminUserId,
        string sessionId,
        string entryId,
        UpdateSettlementEntryRequest request,
        CancellationToken cancellationToken = default)
    {
        if (!IsValidSlotFactor(request.SlotFactor))
        {
            return ServiceResult<SettlementResponse>.Failure(
                StatusCodes.Status400BadRequest,
                "SlotFactor chỉ nhận 1.0 hoặc 0.5.");
        }

        var settlement = await LoadOwnedSettlementAsync(adminUserId, sessionId, cancellationToken, tracking: true);
        if (settlement is null)
            return ServiceResult<SettlementResponse>.Failure(StatusCodes.Status404NotFound, "Chưa có sổ tiền cho trận này.");
        if (settlement.IsClosed)
            return ServiceResult<SettlementResponse>.Failure(StatusCodes.Status409Conflict, "Sổ tiền đang đóng. Mở lại trước khi sửa.");

        var entry = settlement.Entries.SingleOrDefault(item => item.Id == entryId);
        if (entry is null)
            return ServiceResult<SettlementResponse>.Failure(StatusCodes.Status404NotFound, "Không tìm thấy dòng thu tiền.");

        entry.SlotFactor = request.SlotFactor;
        entry.PaymentStatus = request.IsPaid ? SettlementPaymentStatus.Paid : SettlementPaymentStatus.Unpaid;
        entry.UpdatedAt = DateTimeOffset.UtcNow;
        settlement.UpdatedAt = entry.UpdatedAt;
        await db.SaveChangesAsync(cancellationToken);
        return ServiceResult<SettlementResponse>.Success(ToResponse(settlement));
    }

    public async Task<ServiceResult<SettlementResponse>> SetClosedAsync(
        string adminUserId,
        string sessionId,
        bool isClosed,
        CancellationToken cancellationToken = default)
    {
        var settlement = await LoadOwnedSettlementAsync(adminUserId, sessionId, cancellationToken, tracking: true);
        if (settlement is null)
            return ServiceResult<SettlementResponse>.Failure(StatusCodes.Status404NotFound, "Chưa có sổ tiền cho trận này.");

        var now = DateTimeOffset.UtcNow;
        settlement.IsClosed = isClosed;
        settlement.ClosedAt = isClosed ? now : null;
        settlement.UpdatedAt = now;
        await db.SaveChangesAsync(cancellationToken);
        return ServiceResult<SettlementResponse>.Success(ToResponse(settlement));
    }

    public async Task<ServiceResult<SettlementActionResponse>> QueueActionAsync(
        string adminUserId,
        string sessionId,
        CreateSettlementActionRequest request,
        CancellationToken cancellationToken = default)
    {
        if (!Enum.IsDefined(request.Kind))
        {
            return ServiceResult<SettlementActionResponse>.Failure(
                StatusCodes.Status400BadRequest,
                "kind không hợp lệ.");
        }
        if (!Enum.IsDefined(request.Audience))
        {
            return ServiceResult<SettlementActionResponse>.Failure(
                StatusCodes.Status400BadRequest,
                "audience không hợp lệ.");
        }

        var clientRequestId = request.ClientRequestId?.Trim();
        if (clientRequestId is { Length: > 100 })
        {
            return ServiceResult<SettlementActionResponse>.Failure(
                StatusCodes.Status400BadRequest,
                "clientRequestId tối đa 100 ký tự.");
        }
        if (string.IsNullOrWhiteSpace(clientRequestId)) clientRequestId = null;

        var delayMinutes = request.DelayMinutes ?? 0;
        if (delayMinutes is < 0 or > MaxDelayMinutes)
        {
            return ServiceResult<SettlementActionResponse>.Failure(
                StatusCodes.Status400BadRequest,
                $"delayMinutes phải từ 0 đến {MaxDelayMinutes}.");
        }

        var customMessage = CleanMessage(request.Message);
        if (request.Kind == SettlementActionKind.Custom && customMessage is null)
        {
            return ServiceResult<SettlementActionResponse>.Failure(
                StatusCodes.Status400BadRequest,
                "Action Custom cần message.");
        }

        var settlement = await db.MatchSettlements
            .Include(item => item.Session)
                .ThenInclude(session => session.ZaloConnection)
            .Include(item => item.Entries)
            .SingleOrDefaultAsync(
                item => item.SessionId == sessionId && item.Session.AdminUserId == adminUserId,
                cancellationToken);
        if (settlement is null)
            return ServiceResult<SettlementActionResponse>.Failure(StatusCodes.Status404NotFound, "Chưa có sổ tiền cho trận này.");
        if (settlement.Session.ZaloConnection is null || string.IsNullOrWhiteSpace(settlement.Session.ZaloGroupId))
        {
            return ServiceResult<SettlementActionResponse>.Failure(
                StatusCodes.Status400BadRequest,
                "Session chưa liên kết Zalo group nên chưa thể gửi.");
        }

        if (clientRequestId is not null)
        {
            var existingAction = await db.SettlementMessages.AsNoTracking()
                .SingleOrDefaultAsync(
                    item => item.SettlementId == settlement.Id && item.ClientRequestId == clientRequestId,
                    cancellationToken);
            if (existingAction is not null)
                return ServiceResult<SettlementActionResponse>.Success(ToActionResponse(existingAction));
        }
        if (request.IncludePaymentQr && string.IsNullOrWhiteSpace(settlement.Session.PaymentQrImageUrl))
        {
            return ServiceResult<SettlementActionResponse>.Failure(
                StatusCodes.Status400BadRequest,
                "Session chưa cấu hình QR thanh toán.");
        }

        var selectedIds = (request.SelectedEntryIds ?? [])
            .Where(id => !string.IsNullOrWhiteSpace(id))
            .Select(id => id.Trim())
            .Distinct(StringComparer.Ordinal)
            .ToList();
        if (request.Audience == SettlementAudience.Selected)
        {
            if (selectedIds.Count == 0)
            {
                return ServiceResult<SettlementActionResponse>.Failure(
                    StatusCodes.Status400BadRequest,
                    "Audience Selected cần selectedEntryIds.");
            }
            var known = settlement.Entries.Select(item => item.Id).ToHashSet(StringComparer.Ordinal);
            if (selectedIds.Any(id => !known.Contains(id)))
            {
                return ServiceResult<SettlementActionResponse>.Failure(
                    StatusCodes.Status400BadRequest,
                    "selectedEntryIds có dòng không thuộc sổ tiền này.");
            }
        }
        else
        {
            selectedIds.Clear();
        }

        var now = DateTimeOffset.UtcNow;
        var action = new SettlementMessage
        {
            SettlementId = settlement.Id,
            ClientRequestId = clientRequestId,
            ZaloAccountIdSnapshot = settlement.Session.ZaloConnection.AccountZaloId.Trim(),
            ZaloGroupIdSnapshot = settlement.Session.ZaloGroupId.Trim(),
            Kind = request.Kind,
            Audience = request.Audience,
            SelectedEntryIdsJson = JsonSerializer.Serialize(selectedIds),
            Message = customMessage,
            IncludePaymentQr = request.IncludePaymentQr,
            DueAt = now.AddMinutes(delayMinutes),
            Status = SettlementMessageStatus.Pending,
            CreatedAt = now,
            UpdatedAt = now
        };
        db.SettlementMessages.Add(action);
        try
        {
            await db.SaveChangesAsync(cancellationToken);
        }
        catch (DbUpdateException) when (clientRequestId is not null)
        {
            db.Entry(action).State = EntityState.Detached;
            var existingAction = await db.SettlementMessages.AsNoTracking()
                .SingleOrDefaultAsync(
                    item => item.SettlementId == settlement.Id && item.ClientRequestId == clientRequestId,
                    cancellationToken);
            if (existingAction is not null)
                return ServiceResult<SettlementActionResponse>.Success(ToActionResponse(existingAction));
            throw;
        }

        if (delayMinutes == 0)
            await ProcessActionAsync(action.Id, cancellationToken);

        var refreshed = await db.SettlementMessages.AsNoTracking()
            .SingleAsync(item => item.Id == action.Id, cancellationToken);
        return ServiceResult<SettlementActionResponse>.Created(ToActionResponse(refreshed));
    }

    public async Task<int> ProcessDueAsync(CancellationToken cancellationToken = default)
    {
        var now = DateTimeOffset.UtcNow;
        // Keep DateTimeOffset comparison/ordering out of provider SQL. SQLite does not
        // support ordering DateTimeOffset reliably, while PostgreSQL does. Materialize
        // only active queue rows, then choose the next due batch deterministically in memory.
        var candidates = await db.SettlementMessages.AsNoTracking()
            .Where(item => item.Status == SettlementMessageStatus.Pending ||
                           item.Status == SettlementMessageStatus.Sending)
            .Select(item => new { item.Id, item.Status, item.DueAt, item.LeaseUntil })
            .ToListAsync(cancellationToken);
        var dueIds = candidates
            .Where(item => item.Status == SettlementMessageStatus.Pending
                ? item.DueAt <= now
                : item.LeaseUntil is null || item.LeaseUntil <= now)
            .OrderBy(item => item.DueAt)
            .Select(item => item.Id)
            .Take(20)
            .ToList();

        var completed = 0;
        foreach (var actionId in dueIds)
        {
            if (await ProcessActionAsync(actionId, cancellationToken))
                completed += 1;
        }
        return completed;
    }

    public async Task<bool> ProcessActionAsync(
        string actionId,
        CancellationToken cancellationToken = default)
    {
        var candidate = await db.SettlementMessages.AsNoTracking()
            .SingleOrDefaultAsync(item => item.Id == actionId, cancellationToken);
        if (candidate is null || candidate.Status is SettlementMessageStatus.Sent or SettlementMessageStatus.Skipped or SettlementMessageStatus.Failed)
            return false;

        var now = DateTimeOffset.UtcNow;
        if (candidate.Status == SettlementMessageStatus.Pending && candidate.DueAt > now)
            return false;
        if (candidate.Status == SettlementMessageStatus.Sending && candidate.LeaseUntil > now)
            return false;

        var leaseToken = Guid.NewGuid().ToString("n");
        var claimed = await db.SettlementMessages
            .Where(item => item.Id == actionId &&
                           item.Status == candidate.Status &&
                           item.LeaseToken == candidate.LeaseToken)
            .ExecuteUpdateAsync(updates => updates
                .SetProperty(item => item.Status, SettlementMessageStatus.Sending)
                .SetProperty(item => item.LeaseToken, leaseToken)
                .SetProperty(item => item.LeaseUntil, now.AddMinutes(2))
                .SetProperty(item => item.UpdatedAt, now), cancellationToken);
        if (claimed == 0) return false;

        try
        {
            var action = await db.SettlementMessages
                .Include(item => item.Settlement)
                    .ThenInclude(settlement => settlement.Session)
                        .ThenInclude(session => session.ZaloConnection)
                .Include(item => item.Settlement)
                    .ThenInclude(settlement => settlement.Entries)
                .SingleAsync(item => item.Id == actionId, cancellationToken);

            var session = action.Settlement.Session;
            var accountId = CleanZaloUserId(action.ZaloAccountIdSnapshot)
                ?? CleanZaloUserId(session.ZaloConnection?.AccountZaloId);
            var groupId = CleanZaloUserId(action.ZaloGroupIdSnapshot)
                ?? CleanZaloUserId(session.ZaloGroupId);
            if (accountId is null || groupId is null)
                throw new InvalidOperationException("Settlement message has no Zalo target snapshot.");
            if (action.IncludePaymentQr && string.IsNullOrWhiteSpace(session.PaymentQrImageUrl))
                throw new InvalidOperationException("Payment QR is no longer configured for this session.");

            var selectedIds = ParseSelectedIds(action.SelectedEntryIdsJson);
            var body = BuildActionBody(action, session, action.Settlement);
            var audience = BuildAudienceMessage(action.Audience, action.Settlement.Entries, selectedIds, body);
            if (action.Audience != SettlementAudience.All && audience.TargetEntryIds.Count == 0)
            {
                await MarkTerminalAsync(actionId, leaseToken, SettlementMessageStatus.Skipped, now, cancellationToken);
                return true;
            }

            var send = await bridge.SendGroupMessageAsync(
                accountId,
                groupId,
                audience.Message,
                audience.Mentions,
                imageUrl: action.IncludePaymentQr ? session.PaymentQrImageUrl : null,
                idempotencyKey: $"settlement:{action.Id}");
            if (!send.Sent)
                throw new InvalidOperationException("Zalo bridge did not confirm settlement message send.");

            await MarkTerminalAsync(actionId, leaseToken, SettlementMessageStatus.Sent, DateTimeOffset.UtcNow, cancellationToken);
            return true;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception)
        {
            await MarkFailureAsync(actionId, leaseToken, exception.Message, cancellationToken);
            logger.LogWarning(exception, "Settlement message {ActionId} send failed", actionId);
            return false;
        }
    }

    public static SettlementAudienceMessage BuildAudienceMessage(
        SettlementAudience audience,
        IReadOnlyList<SettlementEntry> allEntries,
        IReadOnlyList<string> selectedEntryIds,
        string body)
    {
        var selected = selectedEntryIds.ToHashSet(StringComparer.Ordinal);
        var targets = audience switch
        {
            SettlementAudience.Unpaid => allEntries.Where(item => item.PaymentStatus == SettlementPaymentStatus.Unpaid),
            SettlementAudience.Paid => allEntries.Where(item => item.PaymentStatus == SettlementPaymentStatus.Paid),
            SettlementAudience.Selected => allEntries.Where(item => selected.Contains(item.Id)),
            _ => allEntries
        };
        var orderedTargets = targets.OrderBy(item => item.DisplayNameSnapshot, StringComparer.OrdinalIgnoreCase).ToList();
        if (audience == SettlementAudience.All)
        {
            return new SettlementAudienceMessage(
                $"@all {body.Trim()}",
                [new BridgeOutgoingMention("-1", 0, 4)],
                orderedTargets.Select(item => item.Id).ToList());
        }

        if (orderedTargets.Count == 0)
            return new SettlementAudienceMessage(body.Trim(), [], []);

        var builder = new StringBuilder();
        var mentions = new List<BridgeOutgoingMention>();
        foreach (var entry in orderedTargets)
        {
            if (builder.Length > 0) builder.Append(' ');
            var displayName = entry.DisplayNameSnapshot.Trim().TrimStart('@');
            if (displayName.Length == 0) displayName = "thành viên";
            var label = $"@{displayName}";
            var position = builder.Length;
            builder.Append(label);
            if (!string.IsNullOrWhiteSpace(entry.ZaloUserIdSnapshot))
                mentions.Add(new BridgeOutgoingMention(entry.ZaloUserIdSnapshot.Trim(), position, label.Length));
        }
        if (!string.IsNullOrWhiteSpace(body)) builder.Append(' ').Append(body.Trim());
        return new SettlementAudienceMessage(
            builder.ToString(),
            mentions,
            orderedTargets.Select(item => item.Id).ToList());
    }

    private async Task<MatchSettlement?> LoadOwnedSettlementAsync(
        string adminUserId,
        string sessionId,
        CancellationToken cancellationToken,
        bool tracking = false)
    {
        IQueryable<MatchSettlement> query = db.MatchSettlements
            .Include(item => item.Session)
            .Include(item => item.Entries)
            .Include(item => item.Messages);
        if (!tracking) query = query.AsNoTracking();
        return await query.SingleOrDefaultAsync(
            item => item.SessionId == sessionId && item.Session.AdminUserId == adminUserId,
            cancellationToken);
    }

    private static bool IsValidAmount(decimal amount) =>
        amount > 0m &&
        amount <= 9_999_999_999_999_999.99m &&
        decimal.Round(amount, 2) == amount;

    private static bool IsValidSlotFactor(decimal slotFactor) => slotFactor == 1m || slotFactor == 0.5m;

    private static string? CleanZaloUserId(string? value)
    {
        var cleaned = value?.Trim();
        return string.IsNullOrWhiteSpace(cleaned) ? null : cleaned;
    }

    private static string? CleanMessage(string? value)
    {
        var cleaned = value?.Trim();
        if (string.IsNullOrWhiteSpace(cleaned)) return null;
        return cleaned.Length <= MaxMessageLength ? cleaned : cleaned[..MaxMessageLength];
    }

    private static IReadOnlyList<string> ParseSelectedIds(string json)
    {
        try
        {
            return JsonSerializer.Deserialize<List<string>>(json) ?? [];
        }
        catch (JsonException)
        {
            return [];
        }
    }

    private static string BuildActionBody(
        SettlementMessage action,
        MatchSession session,
        MatchSettlement settlement)
    {
        if (!string.IsNullOrWhiteSpace(action.Message)) return action.Message.Trim();
        var culture = CultureInfo.GetCultureInfo("vi-VN");
        var totalDue = settlement.Entries.Sum(entry => settlement.AmountPerFullSlot * entry.SlotFactor);
        var totalCollected = settlement.Entries
            .Where(entry => entry.PaymentStatus == SettlementPaymentStatus.Paid)
            .Sum(entry => settlement.AmountPerFullSlot * entry.SlotFactor);
        var paidCount = settlement.Entries.Count(entry => entry.PaymentStatus == SettlementPaymentStatus.Paid);
        return action.Kind switch
        {
            SettlementActionKind.Reminder => $"Nhắc tiền sân {session.Name}: ai chưa thanh toán vui lòng chuyển khoản giúp mình nhé.",
            SettlementActionKind.Thanks => $"Cảm ơn mọi người đã thanh toán tiền sân {session.Name} nhé!",
            SettlementActionKind.Summary => $"Tổng kết {session.Name}: đã thu {totalCollected.ToString("N0", culture)}/{totalDue.ToString("N0", culture)}đ, {paidCount}/{settlement.Entries.Count} người đã thanh toán.",
            _ => $"Cập nhật tiền sân {session.Name}."
        };
    }

    private async Task MarkTerminalAsync(
        string actionId,
        string leaseToken,
        SettlementMessageStatus status,
        DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        await db.SettlementMessages
            .Where(item => item.Id == actionId && item.LeaseToken == leaseToken)
            .ExecuteUpdateAsync(updates => updates
                .SetProperty(item => item.Status, status)
                .SetProperty(item => item.SentAt, status == SettlementMessageStatus.Sent ? now : (DateTimeOffset?)null)
                .SetProperty(item => item.LeaseToken, (string?)null)
                .SetProperty(item => item.LeaseUntil, (DateTimeOffset?)null)
                .SetProperty(item => item.LastError, (string?)null)
                .SetProperty(item => item.UpdatedAt, now), cancellationToken);
    }

    private async Task MarkFailureAsync(
        string actionId,
        string leaseToken,
        string error,
        CancellationToken cancellationToken)
    {
        var current = await db.SettlementMessages.AsNoTracking()
            .SingleOrDefaultAsync(item => item.Id == actionId && item.LeaseToken == leaseToken, cancellationToken);
        if (current is null) return;
        var failures = current.FailureCount + 1;
        var now = DateTimeOffset.UtcNow;
        var terminal = failures >= MaxSendAttempts;
        await db.SettlementMessages
            .Where(item => item.Id == actionId && item.LeaseToken == leaseToken)
            .ExecuteUpdateAsync(updates => updates
                .SetProperty(item => item.Status, terminal ? SettlementMessageStatus.Failed : SettlementMessageStatus.Pending)
                .SetProperty(item => item.FailureCount, failures)
                .SetProperty(item => item.LastError, Truncate(error, 1000))
                .SetProperty(item => item.DueAt, terminal ? current.DueAt : now.AddMinutes(Math.Min(15, failures * 2)))
                .SetProperty(item => item.LeaseToken, (string?)null)
                .SetProperty(item => item.LeaseUntil, (DateTimeOffset?)null)
                .SetProperty(item => item.UpdatedAt, now), cancellationToken);
    }

    private static string Truncate(string value, int maxLength) =>
        value.Length <= maxLength ? value : value[..maxLength];

    private static ServiceResult<SettlementResponse> InvalidAmount() =>
        ServiceResult<SettlementResponse>.Failure(
            StatusCodes.Status400BadRequest,
            "amountPerFullSlot phải lớn hơn 0, tối đa 16 chữ số phần nguyên và 2 chữ số thập phân.");

    private static SettlementResponse ToResponse(MatchSettlement settlement)
    {
        var entries = settlement.Entries
            .OrderBy(item => item.DisplayNameSnapshot, StringComparer.OrdinalIgnoreCase)
            .Select(item => new SettlementEntryResponse(
                item.Id,
                item.SessionPlayerId,
                item.DisplayNameSnapshot,
                item.ZaloUserIdSnapshot,
                item.SlotFactor,
                item.PaymentStatus,
                item.PaymentStatus == SettlementPaymentStatus.Paid,
                settlement.AmountPerFullSlot * item.SlotFactor,
                item.UpdatedAt))
            .ToList();
        var totalDue = entries.Sum(item => item.AmountDue);
        var totalCollected = entries.Where(item => item.IsPaid).Sum(item => item.AmountDue);
        var pending = settlement.Messages
            .Where(item => item.Status is SettlementMessageStatus.Pending or SettlementMessageStatus.Sending)
            .OrderBy(item => item.DueAt)
            .Select(ToActionResponse)
            .ToList();
        return new SettlementResponse(
            settlement.Id,
            settlement.SessionId,
            settlement.Session.Name,
            settlement.AmountPerFullSlot,
            settlement.Session.PaymentQrImageUrl,
            settlement.IsClosed,
            settlement.ClosedAt,
            totalDue,
            totalCollected,
            entries.Count(item => item.IsPaid),
            entries.Count,
            entries,
            pending,
            settlement.CreatedAt,
            settlement.UpdatedAt);
    }

    private static SettlementActionResponse ToActionResponse(SettlementMessage action) =>
        new(
            action.Id,
            action.Kind,
            action.Audience,
            action.Message,
            action.IncludePaymentQr,
            action.DueAt,
            action.Status,
            action.FailureCount,
            action.LastError,
            action.SentAt);
}

public sealed record SettlementAudienceMessage(
    string Message,
    IReadOnlyList<BridgeOutgoingMention> Mentions,
    IReadOnlyList<string> TargetEntryIds);

public sealed class SettlementMessageWorker(
    IServiceScopeFactory scopeFactory,
    ILogger<SettlementMessageWorker> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        await Task.Delay(TimeSpan.FromSeconds(5), stoppingToken);
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await using var scope = scopeFactory.CreateAsyncScope();
                var processed = await scope.ServiceProvider.GetRequiredService<SettlementService>()
                    .ProcessDueAsync(stoppingToken);
                if (processed > 0)
                    logger.LogInformation("Settlement message cycle processed {ProcessedCount} action(s)", processed);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception exception)
            {
                logger.LogError(exception, "Settlement message background cycle failed");
            }

            await Task.Delay(TimeSpan.FromSeconds(10), stoppingToken);
        }
    }
}
