using System.Globalization;
using System.Text.RegularExpressions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using VolleyDraft.Api.Contracts;
using VolleyDraft.Api.Models;

namespace VolleyDraft.Api.Services;

internal sealed record ZaloSettlementDateReference(int Day, int Month, int? Year)
{
    internal bool Matches(DateOnly value) =>
        value.Day == Day && value.Month == Month && (Year is null || value.Year == Year.Value);

    internal string Display => Year is null
        ? $"{Day:00}/{Month:00}"
        : $"{Day:00}/{Month:00}/{Year:0000}";
}

internal sealed record ZaloSettlementOpenCommand(
    decimal AmountPerFullSlot,
    ZaloSettlementDateReference? DateReference = null,
    TimeOnly? LocalTime = null,
    string? SelectorError = null);

internal sealed record ZaloSettlementSessionSelection(MatchSession? Session, string? Error)
{
    internal bool IsSuccess => Session is not null;
}

internal static partial class ZaloSettlementCommandParser
{
    private static readonly TimeSpan VietnamOffset = TimeSpan.FromHours(7);
    private static readonly Regex VerificationCode = new(
        @"\bxac\s+minh\s+(?<code>[a-z0-9]{6})\b",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.Compiled);
    private static readonly Regex KAmount = new(
        @"(?<!\d)(?<amount>\d+(?:[\.,]\d+)?)\s*k\b",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.Compiled);
    private static readonly Regex FullAmount = new(
        @"(?<![\d/])(?<amount>\d{4,10})(?!\d)",
        RegexOptions.CultureInvariant | RegexOptions.Compiled);
    private static readonly Regex CalendarDate = new(
        @"(?<!\d)(?<day>[0-3]?\d)[/-](?<month>[01]?\d)(?:[/-](?<year>\d{4}))?(?!\d)",
        RegexOptions.CultureInvariant | RegexOptions.Compiled);
    private static readonly Regex DateLike = new(
        @"(?<!\d)\d{1,2}[/-]\d{1,2}(?:[/-]\d{4})?(?!\d)",
        RegexOptions.CultureInvariant | RegexOptions.Compiled);
    private static readonly Regex ClockTime = new(
        @"(?<!\d)(?<hour>[01]?\d|2[0-3])\s*(?:h|:)\s*(?<minute>[0-5]?\d)?(?!\d)",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.Compiled);

    internal static ZaloSettlementOpenCommand? TryParse(
        ZaloIncomingMessageEvent incoming,
        DateTimeOffset? currentTime = null)
    {
        if (!incoming.MentionedBot) return null;

        var question = ZaloBotService.ExtractQuestion(incoming);
        var normalized = ZaloBotIntelligence.Normalize(question);
        if (!normalized.Contains("so tien", StringComparison.Ordinal) &&
            !normalized.Contains("thu tien", StringComparison.Ordinal))
            return null;

        decimal? amount = null;
        var kMatch = KAmount.Match(normalized);
        if (kMatch.Success && decimal.TryParse(
                kMatch.Groups["amount"].Value.Replace(',', '.'),
                NumberStyles.AllowDecimalPoint,
                CultureInfo.InvariantCulture,
                out var thousands) &&
            thousands > 0)
        {
            amount = thousands * 1000m;
        }
        else
        {
            // Strip calendar-looking fragments before reading a plain numeric amount so the
            // year in "ngày 6/11/2026" can never become a 2.026đ settlement by accident.
            var fullMatch = FullAmount.Match(DateLike.Replace(normalized, " "));
            if (fullMatch.Success && decimal.TryParse(
                    fullMatch.Groups["amount"].Value,
                    NumberStyles.None,
                    CultureInfo.InvariantCulture,
                    out var fullAmount) &&
                fullAmount > 0)
            {
                amount = fullAmount;
            }
        }

        if (amount is null) return null;
        var localNow = (currentTime ?? DateTimeOffset.UtcNow).ToOffset(VietnamOffset);
        var dateReference = TryReadDateReference(normalized, localNow);
        var selectorError = DateLike.IsMatch(normalized) && dateReference is null
            ? "Ngày trong lệnh thu tiền không hợp lệ. Ghi theo dạng ngày 6/11 hoặc 6/11/2026."
            : null;
        return new ZaloSettlementOpenCommand(
            amount.Value,
            dateReference,
            TryReadClock(normalized),
            selectorError);
    }

    internal static string? TryParseVerification(ZaloIncomingMessageEvent incoming)
    {
        if (!incoming.MentionedBot) return null;
        var normalized = ZaloBotIntelligence.Normalize(ZaloBotService.ExtractQuestion(incoming));
        var match = VerificationCode.Match(normalized);
        return match.Success
            ? SettlementAccessService.NormalizeCode(match.Groups["code"].Value)
            : null;
    }

    private static ZaloSettlementDateReference? TryReadDateReference(
        string normalized,
        DateTimeOffset localNow)
    {
        DateOnly? relative = null;
        if (Regex.IsMatch(normalized, @"(?<![a-z])(?:hom\s+qua|bua\s+qua|hqua)(?![a-z])"))
            relative = DateOnly.FromDateTime(localNow.Date.AddDays(-1));
        else if (Regex.IsMatch(normalized, @"(?<![a-z])(?:hom\s+nay|bua\s+nay)(?![a-z])"))
            relative = DateOnly.FromDateTime(localNow.Date);
        else if (Regex.IsMatch(normalized, @"(?<![a-z])ngay\s+mai(?![a-z])"))
            relative = DateOnly.FromDateTime(localNow.Date.AddDays(1));

        if (relative is { } relativeDate)
            return new ZaloSettlementDateReference(relativeDate.Day, relativeDate.Month, relativeDate.Year);

        var match = CalendarDate.Match(normalized);
        if (!match.Success) return null;
        if (!int.TryParse(match.Groups["day"].Value, NumberStyles.None, CultureInfo.InvariantCulture, out var day) ||
            !int.TryParse(match.Groups["month"].Value, NumberStyles.None, CultureInfo.InvariantCulture, out var month))
            return null;
        int? year = null;
        if (match.Groups["year"].Success &&
            int.TryParse(match.Groups["year"].Value, NumberStyles.None, CultureInfo.InvariantCulture, out var parsedYear))
            year = parsedYear;

        var validationYear = year ?? 2000;
        if (validationYear is < 1 or > 9999 ||
            !DateOnly.TryParseExact(
                $"{day:00}/{month:00}/{validationYear:0000}",
                "dd/MM/yyyy",
                CultureInfo.InvariantCulture,
                DateTimeStyles.None,
                out _))
            return null;
        return new ZaloSettlementDateReference(day, month, year);
    }

    private static TimeOnly? TryReadClock(string normalized)
    {
        var match = ClockTime.Match(normalized);
        if (!match.Success) return null;
        var hour = int.Parse(match.Groups["hour"].Value, CultureInfo.InvariantCulture);
        var minute = match.Groups["minute"].Success && match.Groups["minute"].Value.Length > 0
            ? int.Parse(match.Groups["minute"].Value, CultureInfo.InvariantCulture)
            : 0;
        return new TimeOnly(hour, minute);
    }
}

internal static class ZaloSettlementSessionSelector
{
    private static readonly TimeSpan VietnamOffset = TimeSpan.FromHours(7);

    internal static ZaloSettlementSessionSelection Select(
        IReadOnlyList<MatchSession> candidates,
        ZaloSettlementOpenCommand command,
        DateTimeOffset now)
    {
        if (candidates.Count == 0)
            return new ZaloSettlementSessionSelection(null, "Mình chưa thấy trận nào đang gắn với nhóm này để mở sổ tiền.");

        if (command.DateReference is { } dateReference)
        {
            var onDate = candidates
                .Where(item => item.StartTime is not null &&
                               dateReference.Matches(DateOnly.FromDateTime(item.StartTime.Value.ToOffset(VietnamOffset).Date)))
                .OrderBy(item => item.StartTime)
                .ThenByDescending(item => item.UpdatedAt)
                .ToList();
            if (onDate.Count == 0)
            {
                return new ZaloSettlementSessionSelection(
                    null,
                    $"Không tìm thấy trận ngày {dateReference.Display} trong nhóm này.");
            }

            if (dateReference.Year is null)
            {
                var years = onDate
                    .Select(item => item.StartTime!.Value.ToOffset(VietnamOffset).Year)
                    .Distinct()
                    .OrderBy(item => item)
                    .ToList();
                if (years.Count > 1)
                {
                    return new ZaloSettlementSessionSelection(
                        null,
                        $"Ngày {dateReference.Display} có trận ở nhiều năm ({string.Join(", ", years)}). Ghi rõ năm, ví dụ `ngày {dateReference.Display}/{years[^1]}`.");
                }
            }

            if (command.LocalTime is { } targetTime)
            {
                var exactTime = onDate
                    .Where(item => TimeOnly.FromDateTime(item.StartTime!.Value.ToOffset(VietnamOffset).DateTime) == targetTime)
                    .OrderByDescending(item => item.UpdatedAt)
                    .ToList();
                if (exactTime.Count == 1)
                    return new ZaloSettlementSessionSelection(exactTime[0], null);
                if (exactTime.Count == 0)
                {
                    var available = string.Join(", ", onDate.Take(4).Select(item =>
                        item.StartTime!.Value.ToOffset(VietnamOffset).ToString("HH:mm", CultureInfo.InvariantCulture)));
                    return new ZaloSettlementSessionSelection(
                        null,
                        $"Không có trận lúc {targetTime:HH:mm} ngày {dateReference.Display}. Giờ đang có: {available}.");
                }
            }

            if (onDate.Count == 1)
                return new ZaloSettlementSessionSelection(onDate[0], null);

            var choices = string.Join(", ", onDate.Take(4).Select(item =>
            {
                var local = item.StartTime!.Value.ToOffset(VietnamOffset);
                return $"{local:HH:mm} {item.Name}";
            }));
            return new ZaloSettlementSessionSelection(
                null,
                $"Ngày {dateReference.Display} có {onDate.Count} trận ({choices}). Ghi thêm giờ, ví dụ `@bot thu tiền 22k/người ngày {dateReference.Display} 20h30`.");
        }

        // Settlement is a post-match workflow. Without an explicit date, prefer the latest
        // session that has already started; only fall forward when the group has no past/current match.
        var started = candidates
            .Where(item => item.StartTime is not null && item.StartTime.Value <= now)
            .OrderByDescending(item => item.StartTime)
            .ThenByDescending(item => item.UpdatedAt)
            .FirstOrDefault();
        if (started is not null)
            return new ZaloSettlementSessionSelection(started, null);

        var future = candidates
            .Where(item => item.StartTime is not null)
            .OrderBy(item => item.StartTime)
            .ThenByDescending(item => item.UpdatedAt)
            .FirstOrDefault();
        if (future is not null)
            return new ZaloSettlementSessionSelection(future, null);

        return new ZaloSettlementSessionSelection(
            candidates.OrderByDescending(item => item.UpdatedAt).First(),
            null);
    }
}

internal sealed class ZaloSettlementDeepLinkBuilder(IConfiguration configuration)
{
    internal string? Build(string sessionId)
    {
        // Settlement passwordless access relies on a same-origin HttpOnly cookie. The API
        // Docker image serves the React app too, so prefer its public origin over a legacy
        // standalone admin/static web origin.
        var origin = NormalizePublicOrigin(configuration["Public:BaseUrl"]);
        origin ??= NormalizePublicOrigin(configuration["AdminWeb:BaseUrl"]);
        if (origin is null)
        {
            foreach (var value in configuration.GetSection("Cors:Origins").GetChildren().Select(item => item.Value))
            {
                origin = NormalizePublicOrigin(value);
                if (origin is not null) break;
            }
        }
        if (origin is null) return null;

        var encodedSessionId = Uri.EscapeDataString(sessionId);
        return $"{origin}/app?focus=match-settlement&sessionId={encodedSessionId}#match-settlement";
    }

    private static string? NormalizePublicOrigin(string? value)
    {
        var candidate = (value ?? string.Empty).Trim().TrimEnd('/');
        if (!Uri.TryCreate(candidate, UriKind.Absolute, out var uri)) return null;
        if (uri.Scheme is not ("https" or "http")) return null;
        if (uri.Host.Equals("localhost", StringComparison.OrdinalIgnoreCase) ||
            uri.Host.Equals("127.0.0.1", StringComparison.OrdinalIgnoreCase) ||
            uri.Host.Equals("0.0.0.0", StringComparison.OrdinalIgnoreCase))
            return null;
        return uri.GetLeftPart(UriPartial.Authority);
    }
}

public sealed partial class ZaloOverbookService
{
    internal async Task<bool> TryHandleSettlementCommandPreRouteAsync(
        ZaloIncomingMessageEvent incoming,
        CancellationToken cancellationToken = default)
    {
        var verificationCode = ZaloSettlementCommandParser.TryParseVerification(incoming);
        if (verificationCode is not null)
        {
            var accessService = new SettlementAccessService(db, integration);
            var verification = await accessService.VerifyFromZaloAsync(
                incoming.AccountId,
                incoming.GroupId,
                incoming.SenderId,
                incoming.SenderName,
                verificationCode,
                cancellationToken);
            var reply = verification.IsSuccess
                ? verification.Value!.Message
                : verification.Error ?? "Không xác minh được quyền mở Sổ tiền.";
            await SendSettlementReplyAsync(incoming, reply, cancellationToken);
            return true;
        }

        var referenceNow = incoming.SentAtUnixMs > 0
            ? DateTimeOffset.FromUnixTimeMilliseconds(incoming.SentAtUnixMs)
            : DateTimeOffset.UtcNow;
        var command = ZaloSettlementCommandParser.TryParse(incoming, referenceNow);
        if (command is null) return false;
        if (!string.IsNullOrWhiteSpace(command.SelectorError))
        {
            await SendSettlementReplyAsync(incoming, command.SelectorError, cancellationToken);
            return true;
        }

        var accountId = ZaloOverbookLogic.NormalizeId(incoming.AccountId);
        var groupId = ZaloOverbookLogic.NormalizeId(incoming.GroupId);
        var senderId = ZaloOverbookLogic.NormalizeId(incoming.SenderId);
        if (accountId.Length == 0 || groupId.Length == 0 || senderId.Length == 0) return false;

        var candidates = await db.MatchSessions
            .AsNoTracking()
            .Where(session => session.ZaloConnection != null &&
                              session.ZaloConnection.AccountZaloId == accountId &&
                              session.ZaloGroupId == groupId &&
                              session.BotEnabled &&
                              session.Status != SessionStatus.Cancelled)
            .ToListAsync(cancellationToken);
        if (candidates.Count == 0)
        {
            await SendSettlementReplyAsync(
                incoming,
                "Mình chưa thấy trận nào đang gắn với nhóm này để mở sổ tiền.",
                cancellationToken);
            return true;
        }

        var selection = ZaloSettlementSessionSelector.Select(candidates, command, referenceNow);
        if (!selection.IsSuccess)
        {
            await SendSettlementReplyAsync(
                incoming,
                selection.Error ?? "Chưa xác định được trận cần thu tiền.",
                cancellationToken);
            return true;
        }
        var session = selection.Session!;

        var configuredOperators = ZaloOperatorPermissionCommandService.ParseOperatorIds(session.BotOperatorZaloUserIdsJson);
        if (!configuredOperators.Contains(senderId))
        {
            var authorization = await integration.GetGroupRoleAuthorizationAsync(
                session.AdminUserId,
                session.Id,
                senderId);
            if (!authorization.IsSuccess)
            {
                await SendSettlementReplyAsync(
                    incoming,
                    "Mình chưa xác minh được quyền trưởng/phó nhóm từ Zalo lúc này. Bạn thử lại sau nhé.",
                    cancellationToken);
                return true;
            }
            if (authorization.Value?.CanOperateBot != true)
            {
                await SendSettlementReplyAsync(
                    incoming,
                    "Lệnh mở sổ tiền chỉ dành cho trưởng nhóm, phó nhóm hoặc Zalo operator được admin cấp quyền.",
                    cancellationToken);
                return true;
            }
        }

        var settlementService = new SettlementService(
            db,
            bridge,
            new SettlementSummaryCardRenderer(),
            NullLogger<SettlementService>.Instance);
        var existing = await db.MatchSettlements.AsNoTracking()
            .SingleOrDefaultAsync(item => item.SessionId == session.Id, cancellationToken);
        ServiceResult<SettlementResponse> result;
        if (existing is null)
        {
            result = await settlementService.CreateAsync(
                session.AdminUserId,
                session.Id,
                new CreateSettlementRequest(command.AmountPerFullSlot),
                cancellationToken);
        }
        else
        {
            if (existing.IsClosed)
            {
                result = await settlementService.SetClosedAsync(
                    session.AdminUserId,
                    session.Id,
                    false,
                    cancellationToken);
                if (!result.IsSuccess)
                {
                    await SendSettlementReplyAsync(incoming, result.Error ?? "Không mở lại được sổ tiền.", cancellationToken);
                    return true;
                }
            }
            result = await settlementService.UpdateAsync(
                session.AdminUserId,
                session.Id,
                new UpdateSettlementRequest(command.AmountPerFullSlot),
                cancellationToken);
        }

        if (!result.IsSuccess)
        {
            await SendSettlementReplyAsync(incoming, result.Error ?? "Không mở được sổ tiền.", cancellationToken);
            return true;
        }

        var culture = CultureInfo.GetCultureInfo("vi-VN");
        var link = new ZaloSettlementDeepLinkBuilder(configuration).Build(session.Id);
        var linkText = link is null ? " Mở web admin để cập nhật đã thu/chưa thu." : $" {link}";
        var sessionWhen = session.StartTime is { } startTime
            ? $" ({startTime.ToOffset(TimeSpan.FromHours(7)):dd/MM HH:mm})"
            : string.Empty;
        await SendSettlementReplyAsync(
            incoming,
            $"Đã mở sổ tiền {session.Name}{sessionWhen}: {command.AmountPerFullSlot.ToString("N0", culture)}đ/full slot.{linkText}",
            cancellationToken);
        return true;
    }

    private async Task SendSettlementReplyAsync(
        ZaloIncomingMessageEvent incoming,
        string body,
        CancellationToken cancellationToken)
    {
        var senderId = ZaloOverbookLogic.NormalizeId(incoming.SenderId);
        var senderName = (incoming.SenderName ?? string.Empty).Trim().TrimStart('@');
        var mentions = new List<BridgeOutgoingMention>();
        var message = body;
        if (senderId.Length > 0 && senderName.Length > 0)
        {
            var label = $"@{senderName}";
            message = $"{label} {body}";
            mentions.Add(new BridgeOutgoingMention(senderId, 0, label.Length));
        }
        await bridge.SendGroupMessageAsync(
            incoming.AccountId,
            incoming.GroupId,
            message,
            mentions,
            idempotencyKey: $"settlement-reply:{incoming.MessageId}");
    }
}
