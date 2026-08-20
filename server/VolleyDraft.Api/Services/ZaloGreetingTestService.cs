using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using VolleyDraft.Api.Data;
using VolleyDraft.Api.Models;

namespace VolleyDraft.Api.Services;

public sealed record ZaloGreetingTestPreviewRequest(
    string ConnectionId,
    string GroupId,
    string Kind,
    int? BackgroundId = null);

public sealed record ZaloGreetingTestPreviewResponse(
    string AssetId,
    string Kind,
    string GroupId,
    string GroupName,
    string? GroupAvatarUrl,
    string Message,
    string TestSendMessage,
    string ImageUrl,
    int BackgroundId,
    string Mood,
    bool AffectsProductionSchedule);

public sealed record ZaloGreetingTestSendRequest(
    string ConnectionId,
    string GroupId,
    string Kind,
    string AssetId,
    string Message);

public sealed record ZaloGreetingTestSendResponse(
    bool Sent,
    bool Mock,
    string? MessageId,
    string GroupId,
    string GroupName,
    string Kind,
    string ImageUrl,
    string Message,
    bool AffectsProductionSchedule);

internal static class ZaloGreetingTestPolicy
{
    public static bool TryParseKind(string? value, out ZaloDailyGreetingKind kind)
    {
        if (string.Equals(value?.Trim(), "Morning", StringComparison.OrdinalIgnoreCase))
        {
            kind = ZaloDailyGreetingKind.Morning;
            return true;
        }
        if (string.Equals(value?.Trim(), "Night", StringComparison.OrdinalIgnoreCase))
        {
            kind = ZaloDailyGreetingKind.Night;
            return true;
        }

        kind = default;
        return false;
    }

    public static string AssetPrefix(
        ZaloDailyGreetingKind kind,
        string connectionId,
        string groupId) =>
        $"greeting-test-{kind.ToString().ToLowerInvariant()}-{TargetToken(connectionId, groupId)}-";

    public static bool IsAllowedMessage(ZaloDailyGreetingKind kind, string? message)
    {
        var candidate = message?.Trim();
        if (string.IsNullOrWhiteSpace(candidate)) return false;
        return ZaloDailyGreetingPhraseCatalog.All(kind)
            .Contains(candidate, StringComparer.Ordinal);
    }

    public static string BuildOutboundTestMessage(ZaloDailyGreetingKind kind, string productionMessage) =>
        $"🧪 TEST {kind.ToString().ToUpperInvariant()} · {productionMessage.Trim()}";

    public static bool IsProductionGreetingMessage(string? message, ZaloDailyGreetingKind kind) =>
        ZaloDailyGreetingPhraseCatalog.IsKind(message, kind);

    private static string TargetToken(string connectionId, string groupId)
    {
        var material = $"{connectionId?.Trim()}:{groupId?.Trim()}";
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(material));
        return Convert.ToHexString(hash.AsSpan(0, 8)).ToLowerInvariant();
    }
}

public sealed class ZaloGreetingTestService(
    VolleyDraftDbContext db,
    ZaloBridgeClient bridge,
    ZaloCredentialProtector credentialProtector,
    IConfiguration configuration,
    ILogger<ZaloGreetingTestService> logger)
{
    private static readonly TimeSpan VietnamOffset = TimeSpan.FromHours(7);

    public async Task<ServiceResult<ZaloGreetingTestPreviewResponse>> PreviewAsync(
        string adminUserId,
        ZaloGreetingTestPreviewRequest request,
        CancellationToken cancellationToken = default)
    {
        if (!ZaloGreetingTestPolicy.TryParseKind(request.Kind, out var kind))
        {
            return ServiceResult<ZaloGreetingTestPreviewResponse>.Failure(
                StatusCodes.Status400BadRequest,
                "Kind chỉ nhận Morning hoặc Night.");
        }

        var publicOrigin = ResolvePublicOrigin();
        if (string.IsNullOrWhiteSpace(publicOrigin))
        {
            return ServiceResult<ZaloGreetingTestPreviewResponse>.Failure(
                StatusCodes.Status503ServiceUnavailable,
                "Public:BaseUrl hoặc Zalo:WebhookUrl chưa được cấu hình nên Zalo chưa thể tải ảnh test.");
        }

        var resolved = await ResolveLiveTargetAsync(
            adminUserId,
            request.ConnectionId,
            request.GroupId,
            cancellationToken);
        if (resolved.Error is not null)
            return ServiceResult<ZaloGreetingTestPreviewResponse>.Failure(resolved.StatusCode, resolved.Error);

        var backgroundId = request.BackgroundId ?? RandomNumberGenerator.GetInt32(1, 6);
        if (backgroundId is < 1 or > 5)
        {
            return ServiceResult<ZaloGreetingTestPreviewResponse>.Failure(
                StatusCodes.Status400BadRequest,
                "BackgroundId chỉ nhận giá trị từ 1 đến 5.");
        }

        var selector = RandomNumberGenerator.GetInt32(0, int.MaxValue);
        var mood = kind == ZaloDailyGreetingKind.Night
            ? ZaloDailyGreetingEngine.SelectNightMood(selector)
            : ZaloDailyGreetingEngine.SelectMood(selector);
        var phrasePool = ZaloDailyGreetingPhraseCatalog.All(kind);
        if (phrasePool.Count == 0)
        {
            return ServiceResult<ZaloGreetingTestPreviewResponse>.Failure(
                StatusCodes.Status503ServiceUnavailable,
                "Không có lời chúc phù hợp để tạo preview.");
        }
        var message = phrasePool[RandomNumberGenerator.GetInt32(phrasePool.Count)];

        var copy = kind == ZaloDailyGreetingKind.Night
            ? await new ZaloNightGreetingCardCopyGenerator(configuration, logger)
                .TryGenerateAsync(resolved.Group!.Name, mood, [], cancellationToken)
            : await new ZaloSocialCardCopyGenerator(configuration, logger)
                .TryGenerateAsync(
                    resolved.Group!.Name,
                    kind,
                    mood,
                    await HasMatchTodayAsync(adminUserId, request.ConnectionId, request.GroupId, cancellationToken),
                    [],
                    cancellationToken);

        if (copy is null)
        {
            return ServiceResult<ZaloGreetingTestPreviewResponse>.Failure(
                StatusCodes.Status503ServiceUnavailable,
                "AI chưa tạo được copy hợp lệ cho card. Thử tạo preview lại sau ít giây.");
        }

        byte[] rendered;
        try
        {
            rendered = kind == ZaloDailyGreetingKind.Night
                ? ZaloNightGreetingCardRenderer.Render(backgroundId, resolved.Group.Name, copy)
                : ZaloSocialGreetingCardRenderer.Render(backgroundId, resolved.Group.Name, copy);
        }
        catch (Exception exception) when (exception is ArgumentException or InvalidOperationException)
        {
            logger.LogWarning(exception, "Greeting test renderer failed Kind={Kind} Background={BackgroundId}", kind, backgroundId);
            return ServiceResult<ZaloGreetingTestPreviewResponse>.Failure(
                StatusCodes.Status500InternalServerError,
                "Renderer không tạo được card test.");
        }

        await CleanupOldTestAssetsAsync(adminUserId, cancellationToken);
        var assetPrefix = ZaloGreetingTestPolicy.AssetPrefix(kind, request.ConnectionId, request.GroupId);
        var asset = new ZaloBotImageAsset
        {
            AdminUserId = adminUserId,
            FileName = $"{assetPrefix}{Guid.NewGuid():N}.jpg",
            ContentType = "image/jpeg",
            Size = rendered.LongLength,
            Data = rendered,
            CreatedAt = DateTimeOffset.UtcNow
        };
        db.ZaloBotImageAssets.Add(asset);
        await db.SaveChangesAsync(cancellationToken);

        var imageUrl = BuildPublicUrl(publicOrigin, asset.Id);
        var testSendMessage = ZaloGreetingTestPolicy.BuildOutboundTestMessage(kind, message);
        return ServiceResult<ZaloGreetingTestPreviewResponse>.Success(
            new ZaloGreetingTestPreviewResponse(
                asset.Id,
                kind.ToString(),
                resolved.Group.Id,
                resolved.Group.Name,
                resolved.Group.AvatarUrl,
                message,
                testSendMessage,
                imageUrl,
                backgroundId,
                mood.ToString(),
                AffectsProductionSchedule: false));
    }

    public async Task<ServiceResult<ZaloGreetingTestSendResponse>> SendAsync(
        string adminUserId,
        ZaloGreetingTestSendRequest request,
        CancellationToken cancellationToken = default)
    {
        if (!ZaloGreetingTestPolicy.TryParseKind(request.Kind, out var kind))
        {
            return ServiceResult<ZaloGreetingTestSendResponse>.Failure(
                StatusCodes.Status400BadRequest,
                "Kind chỉ nhận Morning hoặc Night.");
        }
        if (!ZaloGreetingTestPolicy.IsAllowedMessage(kind, request.Message))
        {
            return ServiceResult<ZaloGreetingTestSendResponse>.Failure(
                StatusCodes.Status400BadRequest,
                "Tin nhắn test không khớp catalog greeting do server tạo.");
        }

        var publicOrigin = ResolvePublicOrigin();
        if (string.IsNullOrWhiteSpace(publicOrigin))
        {
            return ServiceResult<ZaloGreetingTestSendResponse>.Failure(
                StatusCodes.Status503ServiceUnavailable,
                "Public:BaseUrl hoặc Zalo:WebhookUrl chưa được cấu hình nên Zalo chưa thể tải ảnh test.");
        }

        var resolved = await ResolveLiveTargetAsync(
            adminUserId,
            request.ConnectionId,
            request.GroupId,
            cancellationToken);
        if (resolved.Error is not null)
            return ServiceResult<ZaloGreetingTestSendResponse>.Failure(resolved.StatusCode, resolved.Error);

        var prefix = ZaloGreetingTestPolicy.AssetPrefix(kind, request.ConnectionId, request.GroupId);
        var asset = await db.ZaloBotImageAssets
            .AsNoTracking()
            .SingleOrDefaultAsync(item =>
                item.Id == request.AssetId &&
                item.AdminUserId == adminUserId &&
                item.FileName.StartsWith(prefix),
                cancellationToken);
        if (asset is null)
        {
            return ServiceResult<ZaloGreetingTestSendResponse>.Failure(
                StatusCodes.Status404NotFound,
                "Không tìm thấy preview greeting test cho đúng tài khoản/group này hoặc preview không thuộc tài khoản hiện tại.");
        }

        var imageUrl = BuildPublicUrl(publicOrigin, asset.Id);
        var outboundMessage = ZaloGreetingTestPolicy.BuildOutboundTestMessage(kind, request.Message);
        if (ZaloGreetingTestPolicy.IsProductionGreetingMessage(outboundMessage, kind))
        {
            return ServiceResult<ZaloGreetingTestSendResponse>.Failure(
                StatusCodes.Status500InternalServerError,
                "Greeting test marker không cô lập được production greeting; đã chặn gửi để bảo vệ lịch chúc thật.");
        }

        try
        {
            var sent = await bridge.SendGroupMessageAsync(
                resolved.Connection!.AccountZaloId,
                resolved.Group!.Id,
                outboundMessage,
                [],
                imageUrl,
                idempotencyKey: $"greeting-test:{kind}:{resolved.Connection.Id}:{resolved.Group.Id}:{asset.Id}");

            logger.LogInformation(
                "Greeting test sent Admin={AdminUserId} Connection={ConnectionId} Group={GroupId} Kind={Kind} Asset={AssetId} Mock={Mock}",
                adminUserId,
                resolved.Connection.Id,
                resolved.Group.Id,
                kind,
                asset.Id,
                sent.Mock);

            return ServiceResult<ZaloGreetingTestSendResponse>.Success(
                new ZaloGreetingTestSendResponse(
                    sent.Sent,
                    sent.Mock,
                    sent.MessageId,
                    resolved.Group.Id,
                    resolved.Group.Name,
                    kind.ToString(),
                    imageUrl,
                    outboundMessage,
                    AffectsProductionSchedule: false));
        }
        catch (Exception exception) when (exception is HttpRequestException or TaskCanceledException)
        {
            logger.LogWarning(
                exception,
                "Greeting test send failed Admin={AdminUserId} Connection={ConnectionId} Group={GroupId} Kind={Kind}",
                adminUserId,
                request.ConnectionId,
                request.GroupId,
                kind);
            return ServiceResult<ZaloGreetingTestSendResponse>.Failure(
                StatusCodes.Status502BadGateway,
                $"Zalo Bridge gửi greeting test thất bại: {exception.Message}");
        }
    }

    private async Task<bool> HasMatchTodayAsync(
        string adminUserId,
        string connectionId,
        string groupId,
        CancellationToken cancellationToken)
    {
        var starts = await db.MatchSessions
            .AsNoTracking()
            .Where(item =>
                item.AdminUserId == adminUserId &&
                item.ZaloConnectionId == connectionId &&
                item.ZaloGroupId == groupId &&
                item.Status != SessionStatus.Cancelled &&
                item.StartTime != null)
            .Select(item => item.StartTime)
            .ToListAsync(cancellationToken);
        var today = DateOnly.FromDateTime(DateTimeOffset.UtcNow.ToOffset(VietnamOffset).Date);
        return starts.Any(start => start is not null &&
            DateOnly.FromDateTime(start.Value.ToOffset(VietnamOffset).Date) == today);
    }

    private async Task<ResolvedTarget> ResolveLiveTargetAsync(
        string adminUserId,
        string connectionId,
        string groupId,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(connectionId) || string.IsNullOrWhiteSpace(groupId))
            return ResolvedTarget.Fail(StatusCodes.Status400BadRequest, "Cần chọn tài khoản Zalo và group.");

        var connection = await db.ZaloConnections
            .SingleOrDefaultAsync(item =>
                item.Id == connectionId &&
                item.AdminUserId == adminUserId,
                cancellationToken);
        if (connection is null)
            return ResolvedTarget.Fail(StatusCodes.Status404NotFound, "Không tìm thấy kết nối Zalo.");
        if (connection.Status != ZaloConnectionStatus.Connected)
            return ResolvedTarget.Fail(StatusCodes.Status409Conflict, "Kết nối Zalo hiện không ở trạng thái Connected.");

        try
        {
            var plaintext = credentialProtector.Unprotect(connection.EncryptedCredentials);
            using var document = JsonDocument.Parse(plaintext);
            var groups = await bridge.GetGroupsAsync(document.RootElement.Clone());
            var group = groups.SingleOrDefault(item => string.Equals(item.Id, groupId, StringComparison.Ordinal));
            if (group is null)
            {
                return ResolvedTarget.Fail(
                    StatusCodes.Status400BadRequest,
                    "Group không tồn tại hoặc tài khoản Zalo này không còn trong group.");
            }

            connection.LastValidatedAt = DateTimeOffset.UtcNow;
            connection.Status = ZaloConnectionStatus.Connected;
            await db.SaveChangesAsync(cancellationToken);
            return ResolvedTarget.Success(connection, group);
        }
        catch (Exception exception) when (exception is HttpRequestException or TaskCanceledException or JsonException or CryptographicException or FormatException)
        {
            logger.LogWarning(exception, "Could not resolve live Zalo greeting-test target Connection={ConnectionId} Group={GroupId}", connectionId, groupId);
            return ResolvedTarget.Fail(
                StatusCodes.Status502BadGateway,
                $"Không đọc được group từ Zalo Bridge: {exception.Message}");
        }
    }

    private async Task CleanupOldTestAssetsAsync(string adminUserId, CancellationToken cancellationToken)
    {
        var cutoff = DateTimeOffset.UtcNow.AddDays(-1);
        var stale = await db.ZaloBotImageAssets
            .Where(item =>
                item.AdminUserId == adminUserId &&
                item.FileName.StartsWith("greeting-test-") &&
                item.CreatedAt < cutoff)
            .OrderBy(item => item.CreatedAt)
            .Take(50)
            .ToListAsync(cancellationToken);
        if (stale.Count == 0) return;
        db.ZaloBotImageAssets.RemoveRange(stale);
        await db.SaveChangesAsync(cancellationToken);
    }

    private string? ResolvePublicOrigin()
    {
        var configured = configuration["Public:BaseUrl"]?.Trim();
        if (Uri.TryCreate(configured, UriKind.Absolute, out var configuredUri))
            return configuredUri.GetLeftPart(UriPartial.Authority).TrimEnd('/');

        var webhook = configuration["Zalo:WebhookUrl"]?.Trim();
        return Uri.TryCreate(webhook, UriKind.Absolute, out var webhookUri)
            ? webhookUri.GetLeftPart(UriPartial.Authority).TrimEnd('/')
            : null;
    }

    private static string BuildPublicUrl(string origin, string assetId) =>
        $"{origin.TrimEnd('/')}/api/public/bot-images/{Uri.EscapeDataString(assetId)}";

    private sealed record ResolvedTarget(
        ZaloConnection? Connection,
        BridgeGroup? Group,
        int StatusCode,
        string? Error)
    {
        public static ResolvedTarget Success(ZaloConnection connection, BridgeGroup group) =>
            new(connection, group, StatusCodes.Status200OK, null);

        public static ResolvedTarget Fail(int statusCode, string error) =>
            new(null, null, statusCode, error);
    }
}
