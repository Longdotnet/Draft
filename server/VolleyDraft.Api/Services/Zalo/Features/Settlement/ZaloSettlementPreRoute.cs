using System.Globalization;
using System.Text.RegularExpressions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using VolleyDraft.Api.Contracts;
using VolleyDraft.Api.Models;

namespace VolleyDraft.Api.Services;

internal sealed record ZaloSettlementOpenCommand(decimal AmountPerFullSlot);

internal static partial class ZaloSettlementCommandParser
{
    private static readonly Regex VerificationCode = new(
        @"\bxac\s+minh\s+(?<code>[a-z0-9]{6})\b",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.Compiled);
    private static readonly Regex KAmount = new(
        @"(?<!\d)(?<amount>\d+(?:[\.,]\d+)?)\s*k\b",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.Compiled);
    private static readonly Regex FullAmount = new(
        @"(?<!\d)(?<amount>\d{4,10})(?!\d)",
        RegexOptions.CultureInvariant | RegexOptions.Compiled);

    internal static ZaloSettlementOpenCommand? TryParse(ZaloIncomingMessageEvent incoming)
    {
        if (!incoming.MentionedBot) return null;

        var question = ZaloBotService.ExtractQuestion(incoming);
        var normalized = ZaloBotIntelligence.Normalize(question);
        if (!normalized.Contains("so tien", StringComparison.Ordinal) &&
            !normalized.Contains("thu tien", StringComparison.Ordinal))
            return null;

        var kMatch = KAmount.Match(normalized);
        if (kMatch.Success && decimal.TryParse(
                kMatch.Groups["amount"].Value.Replace(',', '.'),
                NumberStyles.AllowDecimalPoint,
                CultureInfo.InvariantCulture,
                out var thousands) &&
            thousands > 0)
        {
            return new ZaloSettlementOpenCommand(thousands * 1000m);
        }

        var fullMatch = FullAmount.Match(normalized);
        if (fullMatch.Success && decimal.TryParse(
                fullMatch.Groups["amount"].Value,
                NumberStyles.None,
                CultureInfo.InvariantCulture,
                out var amount) &&
            amount > 0)
        {
            return new ZaloSettlementOpenCommand(amount);
        }

        return null;
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

        var command = ZaloSettlementCommandParser.TryParse(incoming);
        if (command is null) return false;

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

        var now = DateTimeOffset.UtcNow;
        var session = candidates
            .OrderBy(item => item.StartTime is null
                ? double.MaxValue
                : Math.Abs((item.StartTime.Value - now).TotalSeconds))
            .ThenByDescending(item => item.UpdatedAt)
            .First();

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

        var settlementService = new SettlementService(db, bridge, NullLogger<SettlementService>.Instance);
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
        await SendSettlementReplyAsync(
            incoming,
            $"Đã mở sổ tiền {session.Name}: {command.AmountPerFullSlot.ToString("N0", culture)}đ/full slot.{linkText}",
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
