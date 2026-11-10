using System.Data;
using System.Data.Common;
using System.Globalization;
using System.Net;
using System.Security.Cryptography;
using System.Text.RegularExpressions;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using Microsoft.Extensions.Caching.Memory;
using VolleyDraft.Api.Contracts;
using VolleyDraft.Api.Data;
using VolleyDraft.Api.Models;
using VolleyDraft.Api.Security;

namespace VolleyDraft.Api.Services;

public sealed record ZaloTrendMusicRenderCompleteRequest(
    string AccountId,
    string GroupId,
    string ConnectionId,
    string LocalDate,
    string IdempotencyKey,
    string CandidateId,
    string Title,
    string Artist,
    int Rank,
    string VideoBase64,
    string ThumbnailBase64,
    int DurationMs = 20_000,
    int Width = 720,
    int Height = 1280,
    string? SourceUrl = null,
    string? LicenseUrl = null,
    string? LicenseLabel = null,
    string? GreetingText = null,
    string? ClaimToken = null);

public sealed record ZaloTrendMusicRenderProgressRequest(
    string AccountId,
    string GroupId,
    string ConnectionId,
    string LocalDate,
    string IdempotencyKey,
    string State,
    string? Title = null,
    string? Artist = null,
    string? Error = null,
    string? ClaimToken = null);

internal sealed record ZaloTrendMusicCandidate(
    string Id,
    string Title,
    string Artist,
    int Rank,
    string MediaType,
    string MediaUrl,
    string? ArtworkUrl,
    string? TrendLabel,
    string? SourceUrl = null,
    string? LicenseUrl = null,
    string? LicenseLabel = null);

internal sealed record ZaloTrendMusicFeed(IReadOnlyList<ZaloTrendMusicCandidate> Items);

internal sealed record ZaloTrendMusicRenderSpec(
    string Period,
    bool GreetingEnabled,
    string GreetingText,
    string? DisplayTitle,
    string? DisplayArtist,
    string? ArtworkUrl,
    bool ShowLogo);

internal sealed record ZaloTrendMusicRenderEnvelope(
    IReadOnlyList<ZaloTrendMusicCandidate> Candidates,
    ZaloTrendMusicRenderSpec Spec,
    string? SongQuery = null);

internal sealed record ZaloTrendMusicOneShotOverride(
    string Period,
    bool GreetingDisabled,
    string? GreetingText,
    string? DisplayTitle,
    string? DisplayArtist,
    string? ArtworkUrl,
    bool? ShowLogo,
    string? Revision = null);

internal sealed record ZaloTrendMusicSettings(
    bool Enabled,
    string FeedUrl,
    string JamendoClientId,
    string GitHubToken,
    string GitHubRepository,
    string GitHubWorkflow,
    string GitHubRef,
    bool UseDailyRhythm,
    bool ShowZyroLogo,
    int StartHour,
    int EndHour,
    int MaxCandidates,
    int RetryMinutes)
{
    public static ZaloTrendMusicSettings FromConfiguration(IConfiguration configuration)
    {
        return new(
            configuration.GetValue("ZaloBot:Ambient:TrendMusic:Enabled", false),
            configuration["ZaloBot:Ambient:TrendMusic:FeedUrl"]?.Trim() ?? string.Empty,
            configuration["ZaloBot:Ambient:TrendMusic:JamendoClientId"]?.Trim() ?? string.Empty,
            configuration["ZaloBot:Ambient:TrendMusic:GitHubToken"]?.Trim() ?? string.Empty,
            configuration["ZaloBot:Ambient:TrendMusic:GitHubRepository"]?.Trim() ?? "Longdotnet/Draft",
            configuration["ZaloBot:Ambient:TrendMusic:GitHubWorkflow"]?.Trim() ?? "trend-music-render.yml",
            configuration["ZaloBot:Ambient:TrendMusic:GitHubRef"]?.Trim() ?? "main",
            configuration.GetValue("ZaloBot:Ambient:TrendMusic:UseDailyRhythm", true),
            configuration.GetValue("ZaloBot:Ambient:TrendMusic:ShowZyroLogo", true),
            Math.Clamp(configuration.GetValue("ZaloBot:Ambient:TrendMusic:StartHour", 6), 0, 23),
            Math.Clamp(configuration.GetValue("ZaloBot:Ambient:TrendMusic:EndHour", 9), 1, 24),
            Math.Clamp(configuration.GetValue("ZaloBot:Ambient:TrendMusic:MaxCandidates", 5), 1, 8),
            Math.Clamp(configuration.GetValue("ZaloBot:Ambient:TrendMusic:RetryMinutes", 35), 10, 180));
    }

    public bool IsConfigured => Enabled;

    public bool HasMediaSource => true;

    public bool CanDirectDispatch =>
        GitHubToken.Length >= 20 && GitHubRepository.Contains('/', StringComparison.Ordinal);
}

public sealed class ZaloTrendMusicService(
    VolleyDraftDbContext db,
    IHttpClientFactory httpClientFactory,
    ZaloTrendMusicRenderDispatcher renderDispatcher,
    ZaloBridgeClient bridge,
    IMemoryCache memoryCache,
    IConfiguration configuration,
    ILogger<ZaloTrendMusicService> logger)
{
    private static readonly TimeSpan VietnamOffset = TimeSpan.FromHours(7);
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        PropertyNameCaseInsensitive = true
    };
    private readonly ZaloTrendMusicJobStore jobs = new(db);
    private readonly ZaloTrendMusicOverrideStore overrides = new(db);
    private readonly ZaloProactiveMessageStore proactiveStore = new(db);
    private static readonly SemaphoreSlim DailyCandidateCacheLock = new(1, 1);

    public async Task<bool> TryHandleCommandAsync(
        ZaloIncomingMessageEvent incoming,
        CancellationToken cancellationToken = default)
    {
        if (await TryHandleRequestedSongAsync(incoming, cancellationToken)) return true;
        if (await TryHandleManualTestAsync(incoming, cancellationToken)) return true;
        return await TryHandleOneShotOverrideAsync(incoming, cancellationToken);
    }

    private async Task<bool> TryHandleRequestedSongAsync(
        ZaloIncomingMessageEvent incoming,
        CancellationToken cancellationToken)
    {
        var songQuery = ParseRequestedSongQuery(incoming);
        if (songQuery is null) return false;

        if (!TrendMusicInternalAuthorization.IsConfigured(configuration))
        {
            await SendTestReplyAsync(
                incoming,
                "🎵 Trend Music chưa có internal key để worker tìm và dựng bài.",
                cancellationToken);
            return true;
        }

        var settings = ZaloTrendMusicSettings.FromConfiguration(configuration);
        var accountId = ZaloOverbookLogic.NormalizeId(incoming.AccountId);
        var groupId = ZaloOverbookLogic.NormalizeId(incoming.GroupId);
        var senderId = ZaloOverbookLogic.NormalizeId(incoming.SenderId);
        if (accountId.Length == 0 || groupId.Length == 0 || senderId.Length == 0) return true;

        var target = await new ZaloProactiveTargetResolver(db)
            .ResolveTargetAsync(accountId, groupId, cancellationToken);
        if (target is null)
        {
            await SendTestReplyAsync(
                incoming,
                "🎵 Nhóm này chưa được cấu hình làm Zalo target của VolleyDraft.",
                cancellationToken);
            return true;
        }

        var sessions = await db.MatchSessions
            .AsNoTracking()
            .Where(session =>
                session.ZaloConnectionId == target.ConnectionId &&
                session.ZaloGroupId == target.GroupId &&
                session.BotEnabled &&
                session.Status != SessionStatus.Cancelled &&
                session.Status != SessionStatus.Finished)
            .OrderByDescending(session => session.UpdatedAt)
            .ThenByDescending(session => session.StartTime)
            .Take(10)
            .ToListAsync(cancellationToken);

        if (!await IsAuthorizedOperatorAsync(target, sessions, senderId, cancellationToken))
        {
            await SendTestReplyAsync(
                incoming,
                "🎵 Lệnh lấy nhạc chỉ dành cho trưởng nhóm, phó nhóm hoặc Zalo operator của nhóm.",
                cancellationToken);
            return true;
        }

        var localDate = DateOnly.FromDateTime(DateTimeOffset.UtcNow.ToOffset(VietnamOffset).Date)
            .ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
        var messageId = StableInboundMessageToken(incoming);
        var idempotencyKey = $"trend-music:request:{target.ConnectionId}:{target.GroupId}:{messageId}";

        try
        {
            var envelope = new ZaloTrendMusicRenderEnvelope(
                [],
                new ZaloTrendMusicRenderSpec(
                    "manual",
                    GreetingEnabled: false,
                    GreetingText: string.Empty,
                    DisplayTitle: null,
                    DisplayArtist: null,
                    ArtworkUrl: null,
                    ShowLogo: settings.ShowZyroLogo),
                SongQuery: songQuery);

            // Explicit song requests are always queued. The queue worker is the only path
            // that installs/runs spotDL, so automatic DailyRhythm and direct ccMixter renders
            // stay dependency-free and unchanged.
            // Acknowledge before making the queue visible so even a fast worker can never
            // publish the matched-song progress before the user's initial command response.
            await SendTestReplyAsync(
                incoming,
                $"🎵 Đang tìm {songQuery} và dựng video…",
                cancellationToken);

            var queued = await DispatchOrQueueRenderAsync(
                settings,
                target,
                localDate,
                idempotencyKey,
                envelope,
                cancellationToken,
                forceQueue: true);

            if (queued && settings.CanDirectDispatch)
            {
                try
                {
                    await renderDispatcher.WakeQueueWorkerAsync(
                        settings.GitHubToken,
                        settings.GitHubRepository,
                        settings.GitHubWorkflow,
                        settings.GitHubRef,
                        cancellationToken);
                    logger.LogInformation(
                        "Requested-song queue worker woken immediately Group={GroupId} Key={IdempotencyKey}",
                        target.GroupId,
                        idempotencyKey);
                }
                catch (Exception exception) when (exception is not OperationCanceledException)
                {
                    // The durable queue is authoritative. A failed wake-up only means the
                    // scheduled */5 GitHub worker will pick this request up as a fallback.
                    logger.LogWarning(
                        exception,
                        "Requested-song immediate worker wake failed; queued cron fallback remains Group={GroupId} Key={IdempotencyKey}",
                        target.GroupId,
                        idempotencyKey);
                }
            }
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            logger.LogWarning(
                exception,
                "Trend music requested-song dispatch failed Group={GroupId} Query={SongQuery}",
                target.GroupId,
                songQuery);
            await SendTestReplyAsync(
                incoming,
                $"🎵 Chưa xếp được yêu cầu {songQuery} lúc này. Kiểm tra worker rồi thử lại.",
                cancellationToken);
        }

        return true;
    }

    public async Task<bool> TryHandleManualTestAsync(
        ZaloIncomingMessageEvent incoming,
        CancellationToken cancellationToken = default)
    {
        if (!IsManualTestCommand(incoming)) return false;

        var settings = ZaloTrendMusicSettings.FromConfiguration(configuration);
        if (!TrendMusicInternalAuthorization.IsConfigured(configuration))
        {
            await SendTestReplyAsync(
                incoming,
                "🎵 Trend Music chưa có internal key để worker render nhận/trả job.",
                cancellationToken);
            return true;
        }

        var accountId = ZaloOverbookLogic.NormalizeId(incoming.AccountId);
        var groupId = ZaloOverbookLogic.NormalizeId(incoming.GroupId);
        var senderId = ZaloOverbookLogic.NormalizeId(incoming.SenderId);
        if (accountId.Length == 0 || groupId.Length == 0 || senderId.Length == 0) return true;

        var target = await new ZaloProactiveTargetResolver(db)
            .ResolveTargetAsync(accountId, groupId, cancellationToken);
        if (target is null)
        {
            await SendTestReplyAsync(
                incoming,
                "🎵 Nhóm này chưa được cấu hình làm Zalo target của VolleyDraft.",
                cancellationToken);
            return true;
        }

        var sessions = await db.MatchSessions
            .AsNoTracking()
            .Where(session =>
                session.ZaloConnectionId == target.ConnectionId &&
                session.ZaloGroupId == target.GroupId &&
                session.BotEnabled &&
                session.Status != SessionStatus.Cancelled &&
                session.Status != SessionStatus.Finished)
            .OrderByDescending(session => session.UpdatedAt)
            .ThenByDescending(session => session.StartTime)
            .Take(10)
            .ToListAsync(cancellationToken);

        var authorized = await IsAuthorizedOperatorAsync(target, sessions, senderId, cancellationToken);

        if (!authorized)
        {
            await SendTestReplyAsync(
                incoming,
                "🎵 Lệnh test nhạc chỉ dành cho trưởng nhóm, phó nhóm hoặc Zalo operator của nhóm.",
                cancellationToken);
            return true;
        }

        IReadOnlyList<ZaloTrendMusicCandidate> candidates = [];
        try
        {
            candidates = await LoadCandidatesAsync(settings, cancellationToken);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            logger.LogWarning(exception, "Trend music manual test source unavailable; using branded fallback");
            candidates = [];
        }

        var localDate = DateOnly.FromDateTime(DateTimeOffset.UtcNow.ToOffset(VietnamOffset).Date)
            .ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
        var messageId = StableInboundMessageToken(incoming);
        var idempotencyKey = $"trend-music:test:{target.ConnectionId}:{target.GroupId}:{messageId}";

        try
        {
            var envelope = new ZaloTrendMusicRenderEnvelope(
                candidates,
                new ZaloTrendMusicRenderSpec(
                    "manual",
                    GreetingEnabled: false,
                    GreetingText: string.Empty,
                    DisplayTitle: null,
                    DisplayArtist: null,
                    ArtworkUrl: null,
                    ShowLogo: settings.ShowZyroLogo));
            var queued = await DispatchOrQueueRenderAsync(
                settings,
                target,
                localDate,
                idempotencyKey,
                envelope,
                cancellationToken);
            await SendTestReplyAsync(
                incoming,
                queued
                    ? candidates.Count > 0
                        ? $"🎵 Đã xếp bản test {candidates[0].Title} — {candidates[0].Artist}. GitHub worker quét mỗi 5 phút; render xong bot gửi video ngay trong nhóm này."
                        : "🎵 Đã xếp bản dự phòng để test luồng video. GitHub worker quét mỗi 5 phút rồi gửi video lại nhóm này."
                    : candidates.Count > 0
                        ? $"🎵 Đang dựng bản test từ {candidates[0].Title} — {candidates[0].Artist}. Render xong bot sẽ gửi video ngay trong nhóm này."
                        : "🎵 Đang dựng bản dự phòng để test luồng video.",
                cancellationToken);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            logger.LogWarning(exception, "Trend music manual test dispatch failed Group={GroupId}", target.GroupId);
            await SendTestReplyAsync(
                incoming,
                "🎵 Chưa xếp được bản test lúc này. Bot chưa gửi video; kiểm tra GitHub Actions/worker rồi thử lại.",
                cancellationToken);
        }

        return true;
    }

    private async Task<bool> TryHandleOneShotOverrideAsync(
        ZaloIncomingMessageEvent incoming,
        CancellationToken cancellationToken)
    {
        var command = ParseOneShotOverrideCommand(incoming);
        if (command is null) return false;

        var accountId = ZaloOverbookLogic.NormalizeId(incoming.AccountId);
        var groupId = ZaloOverbookLogic.NormalizeId(incoming.GroupId);
        var senderId = ZaloOverbookLogic.NormalizeId(incoming.SenderId);
        if (accountId.Length == 0 || groupId.Length == 0 || senderId.Length == 0) return true;

        var target = await new ZaloProactiveTargetResolver(db)
            .ResolveTargetAsync(accountId, groupId, cancellationToken);
        if (target is null)
        {
            await SendTestReplyAsync(incoming, "🎵 Nhóm này chưa được cấu hình làm Zalo target của VolleyDraft.", cancellationToken);
            return true;
        }

        var sessions = await db.MatchSessions
            .AsNoTracking()
            .Where(session =>
                session.ZaloConnectionId == target.ConnectionId &&
                session.ZaloGroupId == target.GroupId &&
                session.BotEnabled &&
                session.Status != SessionStatus.Cancelled &&
                session.Status != SessionStatus.Finished)
            .OrderByDescending(session => session.UpdatedAt)
            .ThenByDescending(session => session.StartTime)
            .Take(10)
            .ToListAsync(cancellationToken);

        var authorized = await IsAuthorizedOperatorAsync(target, sessions, senderId, cancellationToken);
        if (!authorized)
        {
            await SendTestReplyAsync(
                incoming,
                "🎵 Chỉnh bản nhạc sáng/tối chỉ dành cho trưởng nhóm, phó nhóm hoặc Zalo operator của nhóm.",
                cancellationToken);
            return true;
        }

        string? artworkUrl = null;
        if (command.UseQuotedArtwork)
        {
            var assetOwnerId = sessions
                .Select(session => session.AdminUserId)
                .FirstOrDefault(value => !string.IsNullOrWhiteSpace(value));
            if (string.IsNullOrWhiteSpace(assetOwnerId))
            {
                assetOwnerId = await db.ZaloConnections
                    .AsNoTracking()
                    .Where(connection => connection.Id == target.ConnectionId &&
                                         connection.Status == ZaloConnectionStatus.Connected)
                    .Select(connection => connection.AdminUserId)
                    .FirstOrDefaultAsync(cancellationToken);
            }
            if (string.IsNullOrWhiteSpace(assetOwnerId))
            {
                await SendTestReplyAsync(incoming, "🖼️ Chưa xác định được chủ kết nối Zalo để lưu ảnh này.", cancellationToken);
                return true;
            }
            artworkUrl = await PersistQuotedArtworkAsync(
                incoming,
                assetOwnerId,
                cancellationToken);
            if (artworkUrl is null)
            {
                await SendTestReplyAsync(
                    incoming,
                    "🖼️ Hãy reply trực tiếp vào một ảnh rồi ghi “dùng ảnh này” để bot dùng cho bản nhạc kế tiếp.",
                    cancellationToken);
                return true;
            }
        }

        await overrides.PatchAsync(
            target.ConnectionId,
            target.GroupId,
            command.Period,
            command.GreetingDisabled,
            command.GreetingText,
            command.DisplayTitle,
            command.DisplayArtist,
            command.UseQuotedArtwork,
            artworkUrl,
            command.ShowLogo,
            DateTimeOffset.UtcNow.AddDays(8),
            cancellationToken);

        var when = command.Period == "night" ? "tối nay" : "buổi sáng kế tiếp";
        var parts = new List<string>();
        if (command.GreetingDisabled == true) parts.Add("không lời chúc");
        else if (!string.IsNullOrWhiteSpace(command.GreetingText)) parts.Add("lời chúc riêng");
        if (command.UseQuotedArtwork) parts.Add("ảnh bạn vừa reply");
        if (!string.IsNullOrWhiteSpace(command.DisplayTitle)) parts.Add("tiêu đề riêng");
        if (!string.IsNullOrWhiteSpace(command.DisplayArtist)) parts.Add("tên nghệ sĩ riêng");
        if (command.ShowLogo is not null) parts.Add(command.ShowLogo.Value ? "có logo Zyro" : "không logo");
        await SendTestReplyAsync(
            incoming,
            $"🎵 Đã ghi cho {when}: {string.Join(", ", parts)}. Chỉ áp dụng một lần rồi tự trở về mặc định.",
            cancellationToken);
        return true;
    }

    private async Task<bool> IsAuthorizedOperatorAsync(
        ZaloProactiveTarget target,
        IReadOnlyList<MatchSession> sessions,
        string senderId,
        CancellationToken cancellationToken)
    {
        var authoritativeSession = sessions.FirstOrDefault();
        if (authoritativeSession is not null &&
            ZaloOperatorPermissionCommandService.ParseOperatorIds(authoritativeSession.BotOperatorZaloUserIdsJson)
                .Contains(senderId))
            return true;

        var encryptedCredentials = await db.ZaloConnections
            .AsNoTracking()
            .Where(connection => connection.Id == target.ConnectionId &&
                                 connection.Status == ZaloConnectionStatus.Connected)
            .Select(connection => connection.EncryptedCredentials)
            .FirstOrDefaultAsync(cancellationToken);
        if (string.IsNullOrWhiteSpace(encryptedCredentials)) return false;

        try
        {
            var plaintext = new ZaloCredentialProtector(configuration).Unprotect(encryptedCredentials);
            using var credentialsDocument = JsonDocument.Parse(plaintext);
            var roles = await bridge.GetGroupRolesAsync(credentialsDocument.RootElement.Clone(), target.GroupId);
            var normalizedSender = ZaloOverbookLogic.NormalizeId(senderId);
            return string.Equals(
                       ZaloOverbookLogic.NormalizeId(roles.CreatorId),
                       normalizedSender,
                       StringComparison.Ordinal) ||
                   roles.AdminIds
                       .Select(ZaloOverbookLogic.NormalizeId)
                       .Contains(normalizedSender, StringComparer.Ordinal);
        }
        catch (Exception exception) when (exception is HttpRequestException or TaskCanceledException or
                                          CryptographicException or FormatException or JsonException)
        {
            logger.LogWarning(
                exception,
                "Could not verify live Zalo operator role Connection={ConnectionId} Group={GroupId}",
                target.ConnectionId,
                target.GroupId);
            return false;
        }
    }

    internal sealed record OneShotOverrideCommand(
        string Period,
        bool? GreetingDisabled,
        string? GreetingText,
        string? DisplayTitle,
        string? DisplayArtist,
        bool? ShowLogo,
        bool UseQuotedArtwork);

    internal static OneShotOverrideCommand? ParseOneShotOverrideCommand(ZaloIncomingMessageEvent incoming)
    {
        if (!incoming.MentionedBot) return null;
        var raw = ZaloBotService.ExtractQuestion(incoming).Trim();
        var normalized = ZaloBotIntelligence.Normalize(raw).Trim();
        var controlPrefix = new Regex(
                @"(?i)\b(?:lời\s+chúc|loi\s+chuc|chúc|chuc|ghi|tiêu\s+đề|tieu\s+de|title|nghệ\s+sĩ|nghe\s+si|artist)\s*:")
            .Split(raw, 2)[0];
        var normalizedControl = ZaloBotIntelligence.Normalize(controlPrefix).Trim();
        var period = normalizedControl.Contains("toi nay", StringComparison.Ordinal) ||
                     normalizedControl.Contains("dem nay", StringComparison.Ordinal) ||
                     normalizedControl.Contains("nhac toi", StringComparison.Ordinal)
            ? "night"
            : normalizedControl.Contains("sang mai", StringComparison.Ordinal) ||
              normalizedControl.Contains("sang nay", StringComparison.Ordinal) ||
              normalizedControl.Contains("nhac sang", StringComparison.Ordinal)
                ? "morning"
                : string.Empty;
        if (period.Length == 0) return null;

        bool? greetingDisabled = null;
        if (ContainsAny(normalized, "khong loi chuc", "bo loi chuc", "tat loi chuc"))
            greetingDisabled = true;
        var greetingText = ExtractOverrideField(raw, "lời chúc", "loi chuc", "chúc", "chuc", "ghi");
        if (!string.IsNullOrWhiteSpace(greetingText)) greetingDisabled = false;
        var title = ExtractOverrideField(raw, "tiêu đề", "tieu de", "title");
        var artist = ExtractOverrideField(raw, "nghệ sĩ", "nghe si", "artist");

        bool? showLogo = null;
        if (ContainsAny(normalized, "khong co logo", "khong logo", "bo logo", "tat logo")) showLogo = false;
        else if (ContainsAny(normalized, "bat logo", "co logo", "hien logo")) showLogo = true;

        var useQuotedArtwork = ContainsAny(normalized, "dung anh nay", "lay anh nay");
        if (greetingDisabled is null && greetingText is null && title is null && artist is null &&
            showLogo is null && !useQuotedArtwork)
            return null;
        return new OneShotOverrideCommand(
            period,
            greetingDisabled,
            CleanOverrideText(greetingText, 220),
            CleanOverrideText(title, 120),
            CleanOverrideText(artist, 120),
            showLogo,
            useQuotedArtwork);
    }

    private static bool ContainsAny(string value, params string[] needles) =>
        needles.Any(needle => value.Contains(needle, StringComparison.Ordinal));

    private static string? ExtractOverrideField(string raw, params string[] names)
    {
        const string nextField = @"(?:lời\s+chúc|loi\s+chuc|chúc|chuc|ghi|tiêu\s+đề|tieu\s+de|title|nghệ\s+sĩ|nghe\s+si|artist|logo)";
        const string trailingFlag = @"(?:không\s+có\s+logo|khong\s+co\s+logo|không\s+logo|khong\s+logo|bỏ\s+logo|bo\s+logo|tắt\s+logo|tat\s+logo|có\s+logo|co\s+logo|bật\s+logo|bat\s+logo|hiện\s+logo|hien\s+logo|dùng\s+ảnh\s+này|dung\s+anh\s+nay|lấy\s+ảnh\s+này|lay\s+anh\s+nay|không\s+lời\s+chúc|khong\s+loi\s+chuc)";
        foreach (var name in names)
        {
            var match = Regex.Match(
                raw,
                $@"(?ims)(?:^|[,;\n]|\s)\s*{Regex.Escape(name)}\s*:\s*(?<value>.*?)(?=(?:[,;\n]\s*(?:{nextField}\s*:|{trailingFlag}\b))|$)");
            if (match.Success)
            {
                var value = match.Groups["value"].Value.Trim().TrimEnd(',');
                if (value.Length > 0) return value;
            }
        }
        return null;
    }

    private async Task<string?> PersistQuotedArtworkAsync(
        ZaloIncomingMessageEvent incoming,
        string adminUserId,
        CancellationToken cancellationToken)
    {
        var origin = configuration["Public:BaseUrl"]?.Trim().TrimEnd('/');
        if (!IsHttpsUrl(origin)) return null;
        var sourceUrl = ExtractHttpsUrlFromAttachment(incoming.Quote?.Attachment);
        if (!IsTrustedZaloMediaUrl(sourceUrl)) return null;
        var client = httpClientFactory.CreateClient("ZaloQuotedMedia");
        var response = await GetTrustedZaloMediaAsync(client, sourceUrl!, cancellationToken);
        if (response is null) return null;
        using (response)
        {
            if (!response.IsSuccessStatusCode) return null;
            var contentType = response.Content.Headers.ContentType?.MediaType ?? string.Empty;
            if (!contentType.StartsWith("image/", StringComparison.OrdinalIgnoreCase)) return null;
            if (response.Content.Headers.ContentLength is > 10 * 1024 * 1024) return null;
            await using var input = await response.Content.ReadAsStreamAsync(cancellationToken);
            await using var memory = new MemoryStream();
            var buffer = new byte[64 * 1024];
            while (true)
            {
                var read = await input.ReadAsync(buffer, cancellationToken);
                if (read <= 0) break;
                if (memory.Length + read > 10 * 1024 * 1024) return null;
                await memory.WriteAsync(buffer.AsMemory(0, read), cancellationToken);
            }
            byte[] optimized;
            try
            {
                optimized = ZaloBotImageService.OptimizeForDelivery(memory.ToArray());
            }
            catch (InvalidOperationException)
            {
                return null;
            }
            var asset = new ZaloBotImageAsset
            {
                AdminUserId = adminUserId,
                FileName = $"trend-music-{DateTimeOffset.UtcNow:yyyyMMddHHmmss}.jpg",
                ContentType = "image/jpeg",
                Size = optimized.LongLength,
                Data = optimized,
                CreatedAt = DateTimeOffset.UtcNow
            };
            db.ZaloBotImageAssets.Add(asset);
            await db.SaveChangesAsync(cancellationToken);
            await CleanupTrendMusicArtworkAssetsAsync(DateTimeOffset.UtcNow, cancellationToken);
            return $"{origin}/api/public/bot-images/{asset.Id}";
        }
    }

    private async Task CleanupTrendMusicArtworkAssetsAsync(
        DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        var cutoff = now.AddDays(-10);
        var stale = await db.ZaloBotImageAssets
            .Where(item => item.FileName.StartsWith("trend-music-") && item.CreatedAt < cutoff)
            .OrderBy(item => item.CreatedAt)
            .Take(50)
            .ToListAsync(cancellationToken);
        if (stale.Count == 0) return;

        var removed = 0;
        foreach (var asset in stale)
        {
            var publicPath = $"/api/public/bot-images/{asset.Id}";
            if (await overrides.IsArtworkReferencedAsync(publicPath, now, cancellationToken) ||
                await jobs.IsArtworkReferencedByActiveQueueAsync(publicPath, cancellationToken))
                continue;
            db.ZaloBotImageAssets.Remove(asset);
            removed++;
        }
        if (removed > 0) await db.SaveChangesAsync(cancellationToken);
    }

    private static bool IsTrustedZaloMediaUrl(string? value)
    {
        if (!Uri.TryCreate(value, UriKind.Absolute, out var uri) ||
            !string.Equals(uri.Scheme, Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase))
            return false;
        var host = uri.IdnHost;
        return string.Equals(host, "zalo.me", StringComparison.OrdinalIgnoreCase) ||
               host.EndsWith(".zalo.me", StringComparison.OrdinalIgnoreCase) ||
               string.Equals(host, "zaloapp.com", StringComparison.OrdinalIgnoreCase) ||
               host.EndsWith(".zaloapp.com", StringComparison.OrdinalIgnoreCase) ||
               string.Equals(host, "zadn.vn", StringComparison.OrdinalIgnoreCase) ||
               host.EndsWith(".zadn.vn", StringComparison.OrdinalIgnoreCase) ||
               string.Equals(host, "zdn.vn", StringComparison.OrdinalIgnoreCase) ||
               host.EndsWith(".zdn.vn", StringComparison.OrdinalIgnoreCase);
    }

    private static async Task<HttpResponseMessage?> GetTrustedZaloMediaAsync(
        HttpClient client,
        string sourceUrl,
        CancellationToken cancellationToken)
    {
        var current = new Uri(sourceUrl, UriKind.Absolute);
        for (var redirect = 0; redirect < 4; redirect++)
        {
            if (!IsTrustedZaloMediaUrl(current.ToString())) return null;
            var response = await client.GetAsync(current, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
            if (response.StatusCode is not (HttpStatusCode.Moved or HttpStatusCode.Redirect or
                                            HttpStatusCode.RedirectMethod or HttpStatusCode.TemporaryRedirect or
                                            HttpStatusCode.PermanentRedirect))
                return response;
            var location = response.Headers.Location;
            response.Dispose();
            if (location is null) return null;
            current = location.IsAbsoluteUri ? location : new Uri(current, location);
        }
        return null;
    }

    internal static string? ExtractHttpsUrlFromAttachment(string? attachment)
    {
        if (string.IsNullOrWhiteSpace(attachment)) return null;
        var raw = WebUtility.HtmlDecode(attachment.Trim()).Replace("\\/", "/", StringComparison.Ordinal);
        if (IsHttpsUrl(raw)) return raw;
        try
        {
            using var document = JsonDocument.Parse(raw);
            foreach (var propertyName in new[] { "oriUrl", "normalUrl", "href", "thumbUrl", "thumb" })
            {
                var preferredPropertyUrl = FindHttpsProperty(document.RootElement, propertyName);
                if (preferredPropertyUrl is not null) return preferredPropertyUrl;
            }
            var urls = new List<string>();
            CollectHttpsStrings(document.RootElement, urls);
            var preferred = urls.FirstOrDefault(url => Regex.IsMatch(
                url,
                @"\.(?:jpe?g|png|webp)(?:\?|$)",
                RegexOptions.IgnoreCase | RegexOptions.CultureInvariant));
            return preferred ?? urls.FirstOrDefault();
        }
        catch (JsonException)
        {
            var match = Regex.Match(raw, @"https://[^\s\""'<>]+", RegexOptions.IgnoreCase);
            return match.Success ? match.Value : null;
        }
    }

    private static string? FindHttpsProperty(JsonElement element, string propertyName)
    {
        if (element.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in element.EnumerateArray())
            {
                var value = FindHttpsProperty(item, propertyName);
                if (value is not null) return value;
            }
            return null;
        }
        if (element.ValueKind != JsonValueKind.Object) return null;
        foreach (var property in element.EnumerateObject())
        {
            if (string.Equals(property.Name, propertyName, StringComparison.OrdinalIgnoreCase) &&
                property.Value.ValueKind == JsonValueKind.String)
            {
                var value = property.Value.GetString()?.Trim();
                if (IsHttpsUrl(value)) return value;
            }
        }
        foreach (var property in element.EnumerateObject())
        {
            var nested = FindHttpsProperty(property.Value, propertyName);
            if (nested is not null) return nested;
        }
        return null;
    }

    private static void CollectHttpsStrings(JsonElement element, List<string> output)
    {
        if (element.ValueKind == JsonValueKind.String)
        {
            var value = element.GetString()?.Trim();
            if (IsHttpsUrl(value)) output.Add(value!);
            return;
        }
        if (element.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in element.EnumerateArray()) CollectHttpsStrings(item, output);
            return;
        }
        if (element.ValueKind != JsonValueKind.Object) return;
        foreach (var property in element.EnumerateObject()) CollectHttpsStrings(property.Value, output);
    }

    internal async Task<bool> TryScheduleGreetingAsync(
        ZaloProactiveTarget target,
        ZaloDailyGreetingPlan greeting,
        CancellationToken cancellationToken = default)
    {
        var settings = ZaloTrendMusicSettings.FromConfiguration(configuration);
        if (!settings.Enabled || !settings.UseDailyRhythm ||
            !TrendMusicInternalAuthorization.IsConfigured(configuration))
            return false;

        var period = greeting.Kind == ZaloDailyGreetingKind.Night ? "night" : "morning";
        var localDate = greeting.ServiceDate.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
        var jobDateKey = $"{localDate}|{period}";
        var now = DateTimeOffset.UtcNow;
        if (!await jobs.TryBeginAsync(
                target.ConnectionId,
                target.GroupId,
                jobDateKey,
                now,
                TimeSpan.FromMinutes(settings.RetryMinutes),
                cancellationToken))
            return true;

        IReadOnlyList<ZaloTrendMusicCandidate> candidates;
        try
        {
            candidates = await LoadDailyCandidatesCachedAsync(settings, now, cancellationToken);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            logger.LogWarning(
                exception,
                "Trend music source unavailable for daily greeting Group={GroupId} Period={Period}; using branded fallback",
                target.GroupId,
                period);
            candidates = [];
        }

        var idempotencyKey = $"trend-music:{target.ConnectionId}:{target.GroupId}:{localDate}:{period}";
        ZaloTrendMusicOneShotOverride? oneShot;
        ZaloTrendMusicRenderSpec spec;
        try
        {
            oneShot = await overrides.GetAsync(
                target.ConnectionId,
                target.GroupId,
                period,
                now,
                cancellationToken);
            spec = BuildRenderSpec(greeting, settings, oneShot);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            await jobs.MarkFailedAsync(target.ConnectionId, target.GroupId, jobDateKey, cancellationToken);
            logger.LogWarning(
                exception,
                "Trend music override lookup failed Group={GroupId} Period={Period}; will retry without sending duplicate text",
                target.GroupId,
                period);
            return true;
        }

        bool snapshotInserted;
        try
        {
            snapshotInserted = await DispatchOrQueueRenderAsync(
                settings,
                target,
                localDate,
                idempotencyKey,
                new ZaloTrendMusicRenderEnvelope(candidates, spec),
                cancellationToken,
                forceQueue: true);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            await jobs.MarkFailedAsync(target.ConnectionId, target.GroupId, jobDateKey, cancellationToken);
            logger.LogWarning(
                exception,
                "Trend music daily greeting dispatch failed Group={GroupId} Period={Period}",
                target.GroupId,
                period);
            return true;
        }

        if (oneShot is not null && snapshotInserted)
        {
            try
            {
                await overrides.ConsumeIfUnchangedAsync(
                    target.ConnectionId,
                    target.GroupId,
                    period,
                    oneShot.Revision,
                    cancellationToken);
            }
            catch (Exception exception) when (exception is not OperationCanceledException)
            {
                // The render envelope already contains a durable snapshot of the override. A cleanup
                // failure must never make SocialPresence send a second text greeting for the same slot.
                logger.LogWarning(
                    exception,
                    "Trend music override cleanup failed after durable dispatch Group={GroupId} Period={Period}",
                    target.GroupId,
                    period);
            }
        }
        logger.LogInformation(
            "Trend music daily greeting scheduled Group={GroupId} Date={LocalDate} Period={Period} Candidates={CandidateCount}",
            target.GroupId,
            localDate,
            period,
            candidates.Count);
        return true;
    }

    private async Task<IReadOnlyList<ZaloTrendMusicCandidate>> LoadDailyCandidatesCachedAsync(
        ZaloTrendMusicSettings settings,
        DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        var cacheKey = ("ZaloTrendMusicDailyCandidates", settings.FeedUrl, settings.JamendoClientId, settings.MaxCandidates);
        if (memoryCache.TryGetValue<IReadOnlyList<ZaloTrendMusicCandidate>>(cacheKey, out var cached) && cached is not null)
            return cached;

        await DailyCandidateCacheLock.WaitAsync(cancellationToken);
        try
        {
            if (memoryCache.TryGetValue<IReadOnlyList<ZaloTrendMusicCandidate>>(cacheKey, out cached) && cached is not null)
                return cached;
            try
            {
                var loaded = await LoadCandidatesAsync(settings, cancellationToken);
                memoryCache.Set(cacheKey, loaded, TimeSpan.FromMinutes(15));
                return loaded;
            }
            catch (Exception) when (!cancellationToken.IsCancellationRequested)
            {
                // Cache the explicit fallback briefly so a source outage does not repeat the same
                // timeout sequence for every due group or every short-lived DI scope.
                memoryCache.Set<IReadOnlyList<ZaloTrendMusicCandidate>>(cacheKey, [], TimeSpan.FromMinutes(2));
                throw;
            }
        }
        finally
        {
            DailyCandidateCacheLock.Release();
        }
    }

    private static ZaloTrendMusicRenderSpec BuildRenderSpec(
        ZaloDailyGreetingPlan greeting,
        ZaloTrendMusicSettings settings,
        ZaloTrendMusicOneShotOverride? oneShot)
    {
        var period = greeting.Kind == ZaloDailyGreetingKind.Night ? "night" : "morning";
        var greetingEnabled = oneShot?.GreetingDisabled != true;
        var greetingText = string.IsNullOrWhiteSpace(oneShot?.GreetingText)
            ? greeting.Message.Trim()
            : oneShot!.GreetingText!.Trim();
        return new ZaloTrendMusicRenderSpec(
            period,
            greetingEnabled,
            greetingEnabled ? greetingText : string.Empty,
            CleanOverrideText(oneShot?.DisplayTitle, 120),
            CleanOverrideText(oneShot?.DisplayArtist, 120),
            IsHttpsUrl(oneShot?.ArtworkUrl) ? oneShot!.ArtworkUrl!.Trim() : null,
            oneShot?.ShowLogo ?? settings.ShowZyroLogo);
    }

    public async Task ReconcileAsync(CancellationToken cancellationToken = default)
    {
        var settings = ZaloTrendMusicSettings.FromConfiguration(configuration);
        if (!settings.IsConfigured || !TrendMusicInternalAuthorization.IsConfigured(configuration)) return;
        if (settings.UseDailyRhythm) return;

        var now = DateTimeOffset.UtcNow;
        var local = now.ToOffset(VietnamOffset);
        if (!IsInsideWindow(local.Hour, settings.StartHour, settings.EndHour)) return;

        IReadOnlyList<ZaloTrendMusicCandidate> candidates;
        try
        {
            candidates = await LoadCandidatesAsync(settings, cancellationToken);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            // A trend-source outage must not turn the morning ritual into silence. The
            // renderer treats an empty candidate list as an explicit request to build
            // the deterministic branded fallback.
            logger.LogWarning(exception, "Trend music feed unavailable; dispatching branded fallback");
            candidates = [];
        }
        if (candidates.Count == 0)
            logger.LogWarning("Trend music feed has no usable candidates; dispatching branded fallback");

        var targets = await new ZaloProactiveTargetResolver(db).GetTargetsAsync(cancellationToken);
        var localDate = DateOnly.FromDateTime(local.Date).ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
        var jobDateKey = $"{localDate}|morning";
        foreach (var target in targets.Take(100))
        {
            if (!await jobs.TryBeginAsync(
                    target.ConnectionId,
                    target.GroupId,
                    jobDateKey,
                    now,
                    TimeSpan.FromMinutes(settings.RetryMinutes),
                    cancellationToken))
                continue;

            try
            {
                var idempotencyKey = $"trend-music:{target.ConnectionId}:{target.GroupId}:{localDate}:morning";
                await DispatchOrQueueRenderAsync(
                    settings,
                    target,
                    localDate,
                    idempotencyKey,
                    new ZaloTrendMusicRenderEnvelope(
                        candidates,
                        new ZaloTrendMusicRenderSpec(
                            "morning",
                            GreetingEnabled: false,
                            GreetingText: string.Empty,
                            DisplayTitle: null,
                            DisplayArtist: null,
                            ArtworkUrl: null,
                            ShowLogo: settings.ShowZyroLogo)),
                    cancellationToken);
                logger.LogInformation(
                    "Trend music render scheduled Group={GroupId} Date={LocalDate} Candidates={CandidateCount}",
                    target.GroupId,
                    localDate,
                    candidates.Count);
            }
            catch (Exception exception) when (exception is not OperationCanceledException)
            {
                await jobs.MarkFailedAsync(target.ConnectionId, target.GroupId, jobDateKey, cancellationToken);
                logger.LogWarning(exception, "Trend music render dispatch failed Group={GroupId}", target.GroupId);
            }
        }
    }

    public async Task<bool> CompleteRenderAsync(
        ZaloTrendMusicRenderCompleteRequest request,
        CancellationToken cancellationToken = default)
    {
        var target = await new ZaloProactiveTargetResolver(db)
            .ResolveTargetAsync(request.AccountId, request.GroupId, cancellationToken);
        if (target is null ||
            !string.Equals(target.ConnectionId, request.ConnectionId?.Trim(), StringComparison.Ordinal))
            throw new InvalidOperationException("Trend music callback target is not an active configured Zalo group.");

        if (!DateOnly.TryParseExact(
                request.LocalDate,
                "yyyy-MM-dd",
                CultureInfo.InvariantCulture,
                DateTimeStyles.None,
                out var localDate))
            throw new InvalidOperationException("Trend music callback LocalDate is invalid.");

        var today = DateOnly.FromDateTime(DateTimeOffset.UtcNow.ToOffset(VietnamOffset).Date);
        if (Math.Abs(localDate.DayNumber - today.DayNumber) > 1)
            throw new InvalidOperationException("Trend music callback is stale.");

        if (string.IsNullOrWhiteSpace(request.VideoBase64) || request.VideoBase64.Length > 12_000_000)
            throw new InvalidOperationException("Trend music callback video payload is missing or too large.");
        if (string.IsNullOrWhiteSpace(request.ThumbnailBase64) || request.ThumbnailBase64.Length > 3_000_000)
            throw new InvalidOperationException("Trend music callback thumbnail payload is missing or too large.");

        await proactiveStore.EnsureAsync(cancellationToken);
        var existing = await proactiveStore.GetHistoryAsync(
            target.ConnectionId,
            target.GroupId,
            100,
            cancellationToken);
        var existingTrend = existing.FirstOrDefault(item =>
            string.Equals(item.Lane, ZaloProactiveLane.TrendMusic, StringComparison.Ordinal) &&
            string.Equals(item.IdempotencyKey, request.IdempotencyKey, StringComparison.Ordinal));
        if (existingTrend is not null)
        {
            var duplicatePeriod = PeriodFromIdempotencyKey(request.IdempotencyKey);
            var duplicateManual = duplicatePeriod == "manual";
            var duplicateNow = DateTimeOffset.UtcNow;
            await RecordGreetingHistoryAsync(target, request, cancellationToken);
            await proactiveStore.CommitCooldownAsync(
                target.ConnectionId,
                target.GroupId,
                request.IdempotencyKey,
                duplicateNow.AddMinutes(duplicateManual
                    ? 2
                    : ZaloSocialPresenceSettings.FromConfiguration(configuration).MinBotIntervalMinutes),
                cancellationToken);
            if (!duplicateManual)
            {
                await jobs.MarkSentAsync(
                    target.ConnectionId,
                    target.GroupId,
                    $"{request.LocalDate}|{duplicatePeriod}",
                    existingTrend.ProviderMessageId,
                    cancellationToken);
            }
            await jobs.MarkQueueSentAsync(request.IdempotencyKey, request.ClaimToken, cancellationToken);
            return true;
        }

        var currentClaim = await jobs.IsCurrentQueueClaimAsync(
            request.IdempotencyKey,
            request.ClaimToken,
            cancellationToken);
        var explicitSongRequest = request.IdempotencyKey.StartsWith("trend-music:request:", StringComparison.Ordinal);
        if (currentClaim is false || (explicitSongRequest && currentClaim is not true))
            throw new InvalidOperationException("Trend music callback claim is stale or invalid.");

        var now = DateTimeOffset.UtcNow;
        if (!await proactiveStore.TryAcquireLeaseAsync(
                target.ConnectionId,
                target.GroupId,
                request.IdempotencyKey,
                now,
                TimeSpan.FromMinutes(2),
                cancellationToken))
            return false;

        var caption = BuildCaption(request);
        var providerAccepted = false;
        try
        {
            var response = await bridge.SendGroupVideoAsync(
                target.AccountId,
                target.GroupId,
                caption,
                thumbnailBase64: request.ThumbnailBase64,
                thumbnailFileName: $"trend-{request.LocalDate}-{SanitizeId(request.CandidateId)}.jpg",
                videoBase64: request.VideoBase64,
                videoFileName: $"trend-{request.LocalDate}-{SanitizeId(request.CandidateId)}.mp4",
                durationMs: Math.Clamp(request.DurationMs, 1_000, 60_000),
                width: Math.Clamp(request.Width, 240, 2160),
                height: Math.Clamp(request.Height, 240, 3840),
                idempotencyKey: request.IdempotencyKey,
                cancellationToken: cancellationToken);
            if (!response.Sent)
            {
                await proactiveStore.ReleaseLeaseAsync(
                    target.ConnectionId,
                    target.GroupId,
                    request.IdempotencyKey,
                    cancellationToken);
                return false;
            }

            providerAccepted = true;
            var period = PeriodFromIdempotencyKey(request.IdempotencyKey);
            await proactiveStore.RecordAsync(new ZaloProactiveMessageHistoryData(
                Guid.NewGuid().ToString("N"),
                target.ConnectionId,
                target.GroupId,
                request.LocalDate,
                ZaloProactiveLane.TrendMusic,
                period == "night" ? "NightVideo" : period == "manual" ? "ManualVideo" : "MorningVideo",
                $"song:{SanitizeId(request.CandidateId)}",
                null,
                request.Artist,
                caption,
                now,
                response.MessageId,
                request.IdempotencyKey), cancellationToken);
            await RecordGreetingHistoryAsync(target, request, cancellationToken, response.MessageId);
            var isManualTest = IsManualRenderKey(request.IdempotencyKey);
            var cooldownMinutes = ZaloSocialPresenceSettings.FromConfiguration(configuration).MinBotIntervalMinutes;
            await proactiveStore.CommitCooldownAsync(
                target.ConnectionId,
                target.GroupId,
                request.IdempotencyKey,
                now.AddMinutes(isManualTest ? 2 : cooldownMinutes),
                cancellationToken);
            if (!isManualTest)
            {
                await jobs.MarkSentAsync(
                    target.ConnectionId,
                    target.GroupId,
                    $"{request.LocalDate}|{period}",
                    response.MessageId,
                    cancellationToken);
            }
            await jobs.MarkQueueSentAsync(request.IdempotencyKey, request.ClaimToken, cancellationToken);
            return true;
        }
        catch
        {
            if (!providerAccepted)
            {
                await proactiveStore.ReleaseLeaseAsync(
                    target.ConnectionId,
                    target.GroupId,
                    request.IdempotencyKey,
                    cancellationToken);
            }
            throw;
        }
    }

    public async Task<bool> ReportRenderProgressAsync(
        ZaloTrendMusicRenderProgressRequest request,
        CancellationToken cancellationToken = default)
    {
        var target = await new ZaloProactiveTargetResolver(db)
            .ResolveTargetAsync(request.AccountId, request.GroupId, cancellationToken);
        if (target is null ||
            !string.Equals(target.ConnectionId, request.ConnectionId?.Trim(), StringComparison.Ordinal))
            throw new InvalidOperationException("Trend music progress target is not an active configured Zalo group.");

        if (!DateOnly.TryParseExact(
                request.LocalDate,
                "yyyy-MM-dd",
                CultureInfo.InvariantCulture,
                DateTimeStyles.None,
                out var localDate))
            throw new InvalidOperationException("Trend music progress LocalDate is invalid.");

        var today = DateOnly.FromDateTime(DateTimeOffset.UtcNow.ToOffset(VietnamOffset).Date);
        if (Math.Abs(localDate.DayNumber - today.DayNumber) > 1)
            throw new InvalidOperationException("Trend music progress is stale.");

        var currentClaim = await jobs.IsCurrentQueueClaimAsync(
            request.IdempotencyKey,
            request.ClaimToken,
            cancellationToken);
        if (currentClaim is not true)
            throw new InvalidOperationException("Trend music progress claim is stale or invalid.");

        var idempotencyKey = request.IdempotencyKey?.Trim() ?? string.Empty;
        if (!idempotencyKey.StartsWith("trend-music:request:", StringComparison.Ordinal))
            throw new InvalidOperationException("Trend music progress is only valid for explicit song requests.");

        var state = (request.State ?? string.Empty).Trim().ToLowerInvariant();
        string message;
        string suffix;
        switch (state)
        {
            case "matched":
            {
                var title = CleanOverrideText(request.Title, 120);
                var artist = CleanOverrideText(request.Artist, 120);
                if (title is null || artist is null)
                    throw new InvalidOperationException("Matched trend music progress requires title and artist.");
                message = $"🎵 Tìm thấy {title} — {artist}. Đang dựng video 20s…";
                suffix = "matched";
                break;
            }
            case "failed":
            {
                var title = CleanOverrideText(request.Title, 120) ?? "bài nhạc này";
                message = $"🎵 Chưa lấy được {title} lúc này. Bot sẽ không gửi nhầm sang bài khác.";
                suffix = "failed";
                break;
            }
            default:
                throw new InvalidOperationException("Trend music progress state is invalid.");
        }

        var response = await bridge.SendGroupMessageAsync(
            target.AccountId,
            target.GroupId,
            message,
            [],
            idempotencyKey: $"{idempotencyKey}:{suffix}");
        if (response.Sent && state == "failed")
            await jobs.MarkQueueFailedAsync(idempotencyKey, request.ClaimToken, cancellationToken);
        return response.Sent;
    }

    private async Task RecordGreetingHistoryAsync(
        ZaloProactiveTarget target,
        ZaloTrendMusicRenderCompleteRequest request,
        CancellationToken cancellationToken,
        string? providerMessageId = null)
    {
        var period = PeriodFromIdempotencyKey(request.IdempotencyKey);
        var greeting = CleanOverrideText(request.GreetingText, 220);
        if (period == "manual" || greeting is null) return;
        await proactiveStore.RecordAsync(new ZaloProactiveMessageHistoryData(
            Guid.NewGuid().ToString("N"),
            target.ConnectionId,
            target.GroupId,
            request.LocalDate,
            ZaloProactiveLane.DailyGreeting,
            period == "night" ? "Night" : "Morning",
            $"trend-music:{period}:greeting",
            null,
            null,
            greeting,
            DateTimeOffset.UtcNow,
            providerMessageId,
            $"{request.IdempotencyKey}:greeting"), cancellationToken);
    }

    private async Task<IReadOnlyList<ZaloTrendMusicCandidate>> LoadCandidatesAsync(
        ZaloTrendMusicSettings settings,
        CancellationToken cancellationToken)
    {
        if (!string.IsNullOrWhiteSpace(settings.JamendoClientId))
            return await LoadJamendoCandidatesAsync(settings, cancellationToken);
        if (!settings.FeedUrl.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
            return await LoadCcMixterCandidatesAsync(settings.MaxCandidates, cancellationToken);

        var client = httpClientFactory.CreateClient("TrendMusicFeed");
        using var response = await client.GetAsync(settings.FeedUrl, cancellationToken);
        response.EnsureSuccessStatusCode();
        var json = await response.Content.ReadAsStringAsync(cancellationToken);
        var feed = JsonSerializer.Deserialize<ZaloTrendMusicFeed>(json, JsonOptions);
        return (feed?.Items ?? [])
            .Where(IsUsableCandidate)
            .OrderBy(item => item.Rank <= 0 ? int.MaxValue : item.Rank)
            .Take(settings.MaxCandidates)
            .ToArray();
    }

    private async Task<IReadOnlyList<ZaloTrendMusicCandidate>> LoadCcMixterCandidatesAsync(
        int maxCandidates,
        CancellationToken cancellationToken)
    {
        var client = httpClientFactory.CreateClient("TrendMusicFeed");
        foreach (var since in new[] { "12 weeks ago", "1 year ago", string.Empty })
        {
            using var response = await client.GetAsync(BuildCcMixterUri(maxCandidates, since), cancellationToken);
            response.EnsureSuccessStatusCode();
            var json = await response.Content.ReadAsStringAsync(cancellationToken);
            var candidates = ParseCcMixterCandidates(json, maxCandidates);
            if (candidates.Count > 0)
                return await EnrichCcMixterArtworkAsync(
                    httpClientFactory.CreateClient("TrendMusicArtwork"),
                    candidates,
                    cancellationToken);
        }
        return [];
    }

    private async Task<IReadOnlyList<ZaloTrendMusicCandidate>> EnrichCcMixterArtworkAsync(
        HttpClient client,
        IReadOnlyList<ZaloTrendMusicCandidate> candidates,
        CancellationToken cancellationToken)
    {
        var output = candidates.ToArray();
        var tasks = output
            .Take(Math.Min(3, output.Length))
            .Select(async (candidate, index) =>
            {
                if (!string.IsNullOrWhiteSpace(candidate.ArtworkUrl) ||
                    !Uri.TryCreate(candidate.SourceUrl, UriKind.Absolute, out var sourceUri) ||
                    !string.Equals(sourceUri.Scheme, Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase) ||
                    !IsCcMixterHost(sourceUri.Host))
                    return (index, candidate);
                try
                {
                    using var response = await client.GetAsync(sourceUri, cancellationToken);
                    if (!response.IsSuccessStatusCode) return (index, candidate);
                    var html = await response.Content.ReadAsStringAsync(cancellationToken);
                    var artwork = ExtractSourceArtworkUrl(html, candidate.SourceUrl!);
                    return (index, IsHttpsUrl(artwork) ? candidate with { ArtworkUrl = artwork } : candidate);
                }
                catch (Exception exception) when (exception is not OperationCanceledException)
                {
                    logger.LogDebug(exception, "Could not resolve ccMixter artwork Source={SourceUrl}", candidate.SourceUrl);
                    return (index, candidate);
                }
            })
            .ToArray();
        foreach (var result in await Task.WhenAll(tasks))
            output[result.Item1] = result.Item2;
        return output;
    }

    internal static string? ExtractSourceArtworkUrl(string html, string sourceUrl)
    {
        if (string.IsNullOrWhiteSpace(html) ||
            !Uri.TryCreate(sourceUrl, UriKind.Absolute, out var source) ||
            !IsCcMixterHost(source.Host))
            return null;

        // Prefer social artwork first: on real ccMixter pages the credit/profile avatar can be
        // tiny (for example 93x52) and becomes visibly soft when enlarged to the 520px hero frame.
        foreach (var metaName in new[] { "og:image", "twitter:image", "twitter:image:src" })
        {
            var escaped = Regex.Escape(metaName);
            foreach (var pattern in new[]
                     {
                         $@"<meta[^>]+(?:name|property)\s*=\s*['\""]{escaped}['\""][^>]+content\s*=\s*['\""](?<url>[^'\""]+)['\""]",
                         $@"<meta[^>]+content\s*=\s*['\""](?<url>[^'\""]+)['\""][^>]+(?:name|property)\s*=\s*['\""]{escaped}['\""]"
                     })
            {
                var match = Regex.Match(html, pattern, RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
                if (!match.Success) continue;
                var resolved = ResolveCcMixterArtwork(source, match.Groups["url"].Value);
                if (resolved is not null) return resolved;
            }
        }

        // Fall back to the artist/profile image only when the source page has no social artwork.
        var creditIndex = html.IndexOf("id=\"credit_info\"", StringComparison.OrdinalIgnoreCase);
        if (creditIndex < 0) creditIndex = html.IndexOf("id='credit_info'", StringComparison.OrdinalIgnoreCase);
        if (creditIndex > 0)
        {
            var start = Math.Max(0, creditIndex - 1800);
            var prefix = html[start..creditIndex];
            var matches = Regex.Matches(
                prefix,
                @"<img[^>]+src\s*=\s*['\""](?<url>[^'\""]+)['\""]",
                RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
            for (var index = matches.Count - 1; index >= 0; index--)
            {
                var resolved = ResolveCcMixterArtwork(source, matches[index].Groups["url"].Value);
                if (resolved is not null && resolved.Contains("/content/", StringComparison.OrdinalIgnoreCase))
                    return resolved;
            }
        }

        return null;
    }

    private static string? ResolveCcMixterArtwork(Uri source, string value)
    {
        var raw = WebUtility.HtmlDecode(value.Trim());
        if (!Uri.TryCreate(source, raw, out var resolved) ||
            !string.Equals(resolved.Scheme, Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase) ||
            !IsCcMixterHost(resolved.Host))
            return null;
        return resolved.ToString();
    }

    private static bool IsCcMixterHost(string? host) =>
        string.Equals(host, "ccmixter.org", StringComparison.OrdinalIgnoreCase) ||
        (host?.EndsWith(".ccmixter.org", StringComparison.OrdinalIgnoreCase) ?? false);

    internal static string BuildCcMixterUri(int maxCandidates, string since)
    {
        var requestLimit = Math.Clamp(maxCandidates * 4, 8, 24);
        var parts = new List<string>
        {
            "f=json",
            $"limit={requestLimit}",
            "reqtags=remix",
            "lic=by",
            "sort=rank",
            "ord=DESC",
            "dataview=info"
        };
        if (!string.IsNullOrWhiteSpace(since))
            parts.Add($"sinced={Uri.EscapeDataString(since.Trim())}");
        return "https://ccmixter.org/api/query?" + string.Join('&', parts);
    }

    internal static IReadOnlyList<ZaloTrendMusicCandidate> ParseCcMixterCandidates(
        string json,
        int maxCandidates)
    {
        using var document = JsonDocument.Parse(json);
        var root = document.RootElement;
        var items = root.ValueKind == JsonValueKind.Array
            ? root.EnumerateArray().ToArray()
            : root.ValueKind == JsonValueKind.Object
                ? new[] { root.Clone() }
                : [];

        var output = new List<ZaloTrendMusicCandidate>();
        foreach (var item in items)
        {
            if (output.Count >= Math.Clamp(maxCandidates, 1, 8)) break;
            var id = item.TryGetProperty("upload_id", out var idElement)
                ? idElement.ToString().Trim()
                : string.Empty;
            var title = JsonString(item, "upload_name");
            var artist = JsonString(item, "user_real_name");
            if (string.IsNullOrWhiteSpace(artist)) artist = JsonString(item, "user_name");
            var sourceUrl = JsonString(item, "file_page_url");
            var licenseUrl = JsonString(item, "license_url");
            var rankValue = item.TryGetProperty("qrank", out var rankElement) &&
                            rankElement.TryGetInt32(out var parsedRank)
                ? parsedRank
                : 0;
            var mediaUrl = string.Empty;
            if (item.TryGetProperty("files", out var files) && files.ValueKind == JsonValueKind.Array)
            {
                foreach (var file in files.EnumerateArray())
                {
                    var candidateUrl = JsonString(file, "download_url");
                    var fileName = JsonString(file, "file_name");
                    if (!IsHttpsUrl(candidateUrl) ||
                        !fileName.EndsWith(".mp3", StringComparison.OrdinalIgnoreCase))
                        continue;
                    mediaUrl = candidateUrl;
                    break;
                }
            }

            if (string.IsNullOrWhiteSpace(id) ||
                string.IsNullOrWhiteSpace(title) ||
                string.IsNullOrWhiteSpace(artist) ||
                !IsHttpsUrl(mediaUrl) ||
                !IsHttpsUrl(sourceUrl) ||
                !TryGetPermissiveLicense(licenseUrl, out var licenseLabel))
                continue;

            var displayRank = output.Count + 1;
            output.Add(new ZaloTrendMusicCandidate(
                $"ccmixter-{id}",
                title,
                artist,
                displayRank,
                "audio",
                mediaUrl,
                null,
                $"CCMIXTER RECENT #{displayRank} • {licenseLabel}",
                sourceUrl,
                licenseUrl,
                licenseLabel));
        }

        return output;
    }

    private async Task<IReadOnlyList<ZaloTrendMusicCandidate>> LoadJamendoCandidatesAsync(
        ZaloTrendMusicSettings settings,
        CancellationToken cancellationToken)
    {
        var client = httpClientFactory.CreateClient("TrendMusicFeed");
        var uri = BuildJamendoUri(settings.JamendoClientId, settings.MaxCandidates);
        using var response = await client.GetAsync(uri, cancellationToken);
        response.EnsureSuccessStatusCode();
        var json = await response.Content.ReadAsStringAsync(cancellationToken);
        return ParseJamendoCandidates(json, settings.MaxCandidates);
    }

    internal static string BuildJamendoUri(string clientId, int maxCandidates)
    {
        var requestLimit = Math.Clamp(maxCandidates * 5, 10, 40);
        return "https://api.jamendo.com/v3.0/tracks/?" + string.Join('&', new[]
        {
            $"client_id={Uri.EscapeDataString(clientId.Trim())}",
            "format=json",
            $"limit={requestLimit}",
            "order=popularity_week",
            "groupby=artist_id",
            "content_id_free=true",
            "type=single%20albumtrack",
            "imagesize=600",
            "audiodlformat=mp32",
            "include=licenses"
        });
    }

    internal static IReadOnlyList<ZaloTrendMusicCandidate> ParseJamendoCandidates(string json, int maxCandidates)
    {
        using var document = JsonDocument.Parse(json);
        if (!document.RootElement.TryGetProperty("results", out var results) ||
            results.ValueKind != JsonValueKind.Array)
            return [];

        var output = new List<ZaloTrendMusicCandidate>();
        foreach (var item in results.EnumerateArray())
        {
            if (output.Count >= Math.Clamp(maxCandidates, 1, 8)) break;
            var id = JsonString(item, "id");
            var title = JsonString(item, "name");
            var artist = JsonString(item, "artist_name");
            var audioDownload = JsonString(item, "audiodownload");
            var artwork = JsonString(item, "image");
            if (string.IsNullOrWhiteSpace(artwork)) artwork = JsonString(item, "album_image");
            var sourceUrl = JsonString(item, "shareurl");
            var licenseUrl = JsonString(item, "license_ccurl");
            var downloadAllowed = item.TryGetProperty("audiodownload_allowed", out var downloadAllowedElement) &&
                                  downloadAllowedElement.ValueKind is JsonValueKind.True;
            var contentIdFree = item.TryGetProperty("content_id_free", out var contentIdFreeElement) &&
                                contentIdFreeElement.ValueKind is JsonValueKind.True;

            if (!downloadAllowed || !contentIdFree ||
                string.IsNullOrWhiteSpace(id) || string.IsNullOrWhiteSpace(title) || string.IsNullOrWhiteSpace(artist) ||
                !IsHttpsUrl(audioDownload) || !IsHttpsUrl(artwork) || !IsHttpsUrl(sourceUrl) ||
                !TryGetPermissiveLicense(licenseUrl, out var licenseLabel))
                continue;

            var rank = output.Count + 1;
            output.Add(new ZaloTrendMusicCandidate(
                $"jamendo-{id}",
                title,
                artist,
                rank,
                "audio",
                audioDownload,
                artwork,
                $"JAMENDO WEEKLY #{rank} • {licenseLabel}",
                sourceUrl,
                licenseUrl,
                licenseLabel));
        }
        return output;
    }

    private static string JsonString(JsonElement element, string property) =>
        element.TryGetProperty(property, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString()?.Trim() ?? string.Empty
            : string.Empty;

    private static bool IsHttpsUrl(string? value) =>
        Uri.TryCreate(value, UriKind.Absolute, out var uri) &&
        string.Equals(uri.Scheme, Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase);

    private static bool TryGetPermissiveLicense(string? value, out string label)
    {
        label = string.Empty;
        if (!Uri.TryCreate(value, UriKind.Absolute, out var uri) ||
            uri.Scheme is not ("http" or "https") ||
            !string.Equals(uri.Host, "creativecommons.org", StringComparison.OrdinalIgnoreCase))
            return false;
        var path = uri.AbsolutePath.TrimEnd('/').ToLowerInvariant();
        label = path switch
        {
            "/publicdomain/zero/1.0" => "CC0 1.0",
            "/licenses/by/2.5" => "CC BY 2.5",
            "/licenses/by/3.0" => "CC BY 3.0",
            "/licenses/by/4.0" => "CC BY 4.0",
            _ => string.Empty
        };
        return label.Length > 0;
    }

    private async Task<bool> DispatchOrQueueRenderAsync(
        ZaloTrendMusicSettings settings,
        ZaloProactiveTarget target,
        string localDate,
        string idempotencyKey,
        ZaloTrendMusicRenderEnvelope envelope,
        CancellationToken cancellationToken,
        bool forceQueue = false)
    {
        var renderRequestJson = JsonSerializer.Serialize(envelope, JsonOptions);
        if (forceQueue || !settings.CanDirectDispatch)
        {
            return await jobs.EnqueueAsync(
                target,
                localDate,
                idempotencyKey,
                renderRequestJson,
                cancellationToken);
        }

        await renderDispatcher.DispatchAsync(
            settings.GitHubToken,
            settings.GitHubRepository,
            settings.GitHubWorkflow,
            settings.GitHubRef,
            target.AccountId,
            target.GroupId,
            target.ConnectionId,
            localDate,
            idempotencyKey,
            renderRequestJson,
            cancellationToken);
        return false;
    }

    private static string? CleanOverrideText(string? value, int maxLength)
    {
        var text = string.Join(' ', (value ?? string.Empty)
            .Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
        if (text.Length == 0) return null;
        return text.Length <= maxLength ? text : text[..maxLength];
    }

    private static bool IsUsableCandidate(ZaloTrendMusicCandidate item)
    {
        if (string.IsNullOrWhiteSpace(item.Id) ||
            string.IsNullOrWhiteSpace(item.Title) ||
            string.IsNullOrWhiteSpace(item.Artist) ||
            string.IsNullOrWhiteSpace(item.MediaUrl))
            return false;
        if (!Uri.TryCreate(item.MediaUrl, UriKind.Absolute, out var media) ||
            !string.Equals(media.Scheme, Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase))
            return false;
        if (!string.Equals(item.MediaType, "audio", StringComparison.OrdinalIgnoreCase) &&
            !string.Equals(item.MediaType, "video", StringComparison.OrdinalIgnoreCase))
            return false;
        return string.IsNullOrWhiteSpace(item.ArtworkUrl) ||
               Uri.TryCreate(item.ArtworkUrl, UriKind.Absolute, out var art) &&
               string.Equals(art.Scheme, Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsInsideWindow(int hour, int start, int end) =>
        start < end ? hour >= start && hour < end : hour >= start || hour < end;

    internal static string BuildCaption(ZaloTrendMusicRenderCompleteRequest request)
    {
        var caption = $"🎵 {request.Title.Trim()} — {request.Artist.Trim()}";
        if (IsHttpsUrl(request.SourceUrl) &&
            TryGetPermissiveLicense(request.LicenseUrl, out var canonicalLabel) &&
            (string.IsNullOrWhiteSpace(request.LicenseLabel) ||
             string.Equals(request.LicenseLabel.Trim(), canonicalLabel, StringComparison.OrdinalIgnoreCase)))
        {
            caption += $"\nNguồn: {request.SourceUrl!.Trim()}";
            caption += $"\nLicense: {canonicalLabel} {request.LicenseUrl!.Trim()}";
            caption += "\nĐã cắt/ghép thành video dọc 20s.";
        }
        return caption;
    }

    private static string PeriodFromIdempotencyKey(string? idempotencyKey)
    {
        var value = idempotencyKey?.Trim() ?? string.Empty;
        if (IsManualRenderKey(value)) return "manual";
        if (value.EndsWith(":night", StringComparison.Ordinal)) return "night";
        return "morning";
    }

    private static bool IsManualRenderKey(string? idempotencyKey)
    {
        var value = idempotencyKey?.Trim() ?? string.Empty;
        return value.StartsWith("trend-music:test:", StringComparison.Ordinal) ||
               value.StartsWith("trend-music:request:", StringComparison.Ordinal);
    }

    internal static string? ParseRequestedSongQuery(ZaloIncomingMessageEvent incoming)
    {
        if (!incoming.MentionedBot) return null;
        var raw = ZaloBotService.ExtractQuestion(incoming).Trim();
        if (raw.Length == 0) return null;

        var match = Regex.Match(
            raw,
            @"(?is)^\s*(?:lấy|lay)\s+(?:nhạc|nhac)\s+(?<query>.+?)\s*$",
            RegexOptions.CultureInvariant);
        if (!match.Success) return null;

        var query = string.Join(' ', match.Groups["query"].Value
            .Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
        if (query.Length >= 2)
        {
            var first = query[0];
            var last = query[^1];
            var pairedQuotes = (first, last) is
                ('"', '"') or
                ('\'', '\'') or
                ('“', '”') or
                ('‘', '’');
            if (pairedQuotes)
                query = query[1..^1].Trim();
        }

        if (query.Length is < 2 or > 120) return null;
        return query;
    }

    internal static bool IsManualTestCommand(ZaloIncomingMessageEvent incoming)
    {
        if (!incoming.MentionedBot) return false;
        var normalized = ZaloBotIntelligence.Normalize(ZaloBotService.ExtractQuestion(incoming)).Trim();
        return normalized is "test nhac" or "test nhac trend" or "test trend music" or "thu nhac trend";
    }

    private async Task SendTestReplyAsync(
        ZaloIncomingMessageEvent incoming,
        string message,
        CancellationToken cancellationToken)
    {
        await bridge.SendGroupMessageAsync(
            incoming.AccountId,
            incoming.GroupId,
            message,
            [],
            idempotencyKey: $"trend-music:test-reply:{StableInboundMessageToken(incoming)}:{Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(message)))[..12]}");
    }

    private static string StableInboundMessageToken(ZaloIncomingMessageEvent incoming)
    {
        var providerId = incoming.MessageId?.Trim() ?? string.Empty;
        var seed = providerId.Length > 0
            ? $"provider:{providerId}"
            : $"fallback:{incoming.AccountId}|{incoming.GroupId}|{incoming.SenderId}|{incoming.SentAtUnixMs}|{incoming.Content}";
        return Convert.ToHexString(SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(seed)))[..24]
            .ToLowerInvariant();
    }

    private static string SanitizeId(string? value)
    {
        var text = new string((value ?? string.Empty)
            .Where(ch => char.IsLetterOrDigit(ch) || ch is '-' or '_')
            .Take(48)
            .ToArray());
        return text.Length == 0 ? "music" : text;
    }

    public Task<ZaloTrendMusicRenderClaim?> ClaimQueuedRenderAsync(
        CancellationToken cancellationToken = default) =>
        jobs.TryClaimQueueAsync(
            DateTimeOffset.UtcNow,
            TimeSpan.FromMinutes(10),
            cancellationToken);
}

public sealed record ZaloTrendMusicRenderClaim(
    string AccountId,
    string GroupId,
    string ConnectionId,
    string LocalDate,
    string IdempotencyKey,
    string CandidatesJson,
    string ClaimToken);

public sealed class ZaloTrendMusicWorker(
    IServiceScopeFactory scopeFactory,
    ILogger<ZaloTrendMusicWorker> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await using var scope = scopeFactory.CreateAsyncScope();
                await scope.ServiceProvider.GetRequiredService<ZaloTrendMusicService>()
                    .ReconcileAsync(stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception exception)
            {
                logger.LogError(exception, "Trend music reconciliation failed");
            }

            await Task.Delay(TimeSpan.FromMinutes(5), stoppingToken);
        }
    }
}

internal sealed class ZaloTrendMusicOverrideStore(VolleyDraftDbContext db)
{
    private bool ensured;

    public async Task UpsertAsync(
        string connectionId,
        string groupId,
        ZaloTrendMusicOneShotOverride value,
        DateTimeOffset expiresAt,
        CancellationToken cancellationToken)
    {
        await EnsureAsync(cancellationToken);
        const string sql = """
            INSERT INTO "ZaloTrendMusicOverride" (
                "ConnectionId", "GroupId", "Period", "GreetingDisabled", "GreetingText",
                "DisplayTitle", "DisplayArtist", "ArtworkUrl", "ShowLogo", "ExpiresAt", "UpdatedAt")
            VALUES (
                @ConnectionId, @GroupId, @Period, @GreetingDisabled, @GreetingText,
                @DisplayTitle, @DisplayArtist, @ArtworkUrl, @ShowLogo, @ExpiresAt, @UpdatedAt)
            ON CONFLICT ("ConnectionId", "GroupId", "Period") DO UPDATE SET
                "GreetingDisabled" = excluded."GreetingDisabled",
                "GreetingText" = excluded."GreetingText",
                "DisplayTitle" = excluded."DisplayTitle",
                "DisplayArtist" = excluded."DisplayArtist",
                "ArtworkUrl" = excluded."ArtworkUrl",
                "ShowLogo" = excluded."ShowLogo",
                "ExpiresAt" = excluded."ExpiresAt",
                "UpdatedAt" = excluded."UpdatedAt";
            """;
        await using var command = await CreateCommandAsync(sql, cancellationToken);
        Add(command, "@ConnectionId", connectionId.Trim());
        Add(command, "@GroupId", groupId.Trim());
        Add(command, "@Period", value.Period);
        Add(command, "@GreetingDisabled", value.GreetingDisabled ? 1 : 0);
        Add(command, "@GreetingText", value.GreetingText);
        Add(command, "@DisplayTitle", value.DisplayTitle);
        Add(command, "@DisplayArtist", value.DisplayArtist);
        Add(command, "@ArtworkUrl", value.ArtworkUrl);
        Add(command, "@ShowLogo", value.ShowLogo is null ? null : value.ShowLogo.Value ? 1 : 0);
        Add(command, "@ExpiresAt", expiresAt.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture));
        Add(command, "@UpdatedAt", DateTimeOffset.UtcNow.ToString("O", CultureInfo.InvariantCulture));
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    public async Task PatchAsync(
        string connectionId,
        string groupId,
        string period,
        bool? greetingDisabled,
        string? greetingText,
        string? displayTitle,
        string? displayArtist,
        bool patchArtwork,
        string? artworkUrl,
        bool? showLogo,
        DateTimeOffset expiresAt,
        CancellationToken cancellationToken)
    {
        await EnsureAsync(cancellationToken);
        var patchGreetingDisabled = greetingDisabled.HasValue || !string.IsNullOrWhiteSpace(greetingText);
        var effectiveGreetingDisabled = !string.IsNullOrWhiteSpace(greetingText)
            ? false
            : greetingDisabled ?? false;
        var revision = DateTimeOffset.UtcNow.ToString("O", CultureInfo.InvariantCulture);
        const string sql = """
            INSERT INTO "ZaloTrendMusicOverride" (
                "ConnectionId", "GroupId", "Period", "GreetingDisabled", "GreetingText",
                "DisplayTitle", "DisplayArtist", "ArtworkUrl", "ShowLogo", "ExpiresAt", "UpdatedAt")
            VALUES (
                @ConnectionId, @GroupId, @Period, @GreetingDisabled, @GreetingText,
                @DisplayTitle, @DisplayArtist, @ArtworkUrl, @ShowLogo, @ExpiresAt, @UpdatedAt)
            ON CONFLICT ("ConnectionId", "GroupId", "Period") DO UPDATE SET
                "GreetingDisabled" = CASE WHEN @PatchGreetingDisabled = 1
                    THEN excluded."GreetingDisabled" ELSE "ZaloTrendMusicOverride"."GreetingDisabled" END,
                "GreetingText" = CASE WHEN @PatchGreetingText = 1
                    THEN excluded."GreetingText" ELSE "ZaloTrendMusicOverride"."GreetingText" END,
                "DisplayTitle" = CASE WHEN @PatchDisplayTitle = 1
                    THEN excluded."DisplayTitle" ELSE "ZaloTrendMusicOverride"."DisplayTitle" END,
                "DisplayArtist" = CASE WHEN @PatchDisplayArtist = 1
                    THEN excluded."DisplayArtist" ELSE "ZaloTrendMusicOverride"."DisplayArtist" END,
                "ArtworkUrl" = CASE WHEN @PatchArtwork = 1
                    THEN excluded."ArtworkUrl" ELSE "ZaloTrendMusicOverride"."ArtworkUrl" END,
                "ShowLogo" = CASE WHEN @PatchShowLogo = 1
                    THEN excluded."ShowLogo" ELSE "ZaloTrendMusicOverride"."ShowLogo" END,
                "ExpiresAt" = excluded."ExpiresAt",
                "UpdatedAt" = excluded."UpdatedAt";
            """;
        await using var command = await CreateCommandAsync(sql, cancellationToken);
        Add(command, "@ConnectionId", connectionId.Trim());
        Add(command, "@GroupId", groupId.Trim());
        Add(command, "@Period", period);
        Add(command, "@GreetingDisabled", effectiveGreetingDisabled ? 1 : 0);
        Add(command, "@GreetingText", greetingText);
        Add(command, "@DisplayTitle", displayTitle);
        Add(command, "@DisplayArtist", displayArtist);
        Add(command, "@ArtworkUrl", artworkUrl);
        Add(command, "@ShowLogo", showLogo is null ? null : showLogo.Value ? 1 : 0);
        Add(command, "@ExpiresAt", expiresAt.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture));
        Add(command, "@UpdatedAt", revision);
        Add(command, "@PatchGreetingDisabled", patchGreetingDisabled ? 1 : 0);
        Add(command, "@PatchGreetingText", string.IsNullOrWhiteSpace(greetingText) ? 0 : 1);
        Add(command, "@PatchDisplayTitle", string.IsNullOrWhiteSpace(displayTitle) ? 0 : 1);
        Add(command, "@PatchDisplayArtist", string.IsNullOrWhiteSpace(displayArtist) ? 0 : 1);
        Add(command, "@PatchArtwork", patchArtwork ? 1 : 0);
        Add(command, "@PatchShowLogo", showLogo.HasValue ? 1 : 0);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    public async Task<bool> IsArtworkReferencedAsync(
        string publicPath,
        DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        await EnsureAsync(cancellationToken);
        const string sql = """
            SELECT 1
            FROM "ZaloTrendMusicOverride"
            WHERE "ExpiresAt" > @Now
              AND "ArtworkUrl" LIKE @Pattern
            LIMIT 1;
            """;
        await using var command = await CreateCommandAsync(sql, cancellationToken);
        Add(command, "@Now", now.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture));
        Add(command, "@Pattern", $"%{publicPath}");
        return await command.ExecuteScalarAsync(cancellationToken) is not null;
    }

    public async Task<ZaloTrendMusicOneShotOverride?> GetAsync(
        string connectionId,
        string groupId,
        string period,
        DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        await EnsureAsync(cancellationToken);
        const string sql = """
            SELECT "GreetingDisabled", "GreetingText", "DisplayTitle", "DisplayArtist", "ArtworkUrl", "ShowLogo", "UpdatedAt"
            FROM "ZaloTrendMusicOverride"
            WHERE "ConnectionId" = @ConnectionId
              AND "GroupId" = @GroupId
              AND "Period" = @Period
              AND "ExpiresAt" > @Now
            LIMIT 1;
            """;
        await using var command = await CreateCommandAsync(sql, cancellationToken);
        Add(command, "@ConnectionId", connectionId.Trim());
        Add(command, "@GroupId", groupId.Trim());
        Add(command, "@Period", period);
        Add(command, "@Now", now.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture));
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        if (!await reader.ReadAsync(cancellationToken)) return null;
        bool? showLogo = reader.IsDBNull(5) ? null : reader.GetInt64(5) != 0;
        return new ZaloTrendMusicOneShotOverride(
            period,
            reader.GetInt64(0) != 0,
            reader.IsDBNull(1) ? null : reader.GetString(1),
            reader.IsDBNull(2) ? null : reader.GetString(2),
            reader.IsDBNull(3) ? null : reader.GetString(3),
            reader.IsDBNull(4) ? null : reader.GetString(4),
            showLogo,
            reader.IsDBNull(6) ? null : reader.GetString(6));
    }

    public async Task ConsumeIfUnchangedAsync(
        string connectionId,
        string groupId,
        string period,
        string? revision,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(revision)) return;
        await EnsureAsync(cancellationToken);
        const string sql = """
            DELETE FROM "ZaloTrendMusicOverride"
            WHERE "ConnectionId" = @ConnectionId
              AND "GroupId" = @GroupId
              AND "Period" = @Period
              AND "UpdatedAt" = @Revision;
            """;
        await using var command = await CreateCommandAsync(sql, cancellationToken);
        Add(command, "@ConnectionId", connectionId.Trim());
        Add(command, "@GroupId", groupId.Trim());
        Add(command, "@Period", period);
        Add(command, "@Revision", revision);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    public async Task ConsumeAsync(
        string connectionId,
        string groupId,
        string period,
        CancellationToken cancellationToken)
    {
        await EnsureAsync(cancellationToken);
        const string sql = """
            DELETE FROM "ZaloTrendMusicOverride"
            WHERE "ConnectionId" = @ConnectionId
              AND "GroupId" = @GroupId
              AND "Period" = @Period;
            """;
        await using var command = await CreateCommandAsync(sql, cancellationToken);
        Add(command, "@ConnectionId", connectionId.Trim());
        Add(command, "@GroupId", groupId.Trim());
        Add(command, "@Period", period);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private async Task EnsureAsync(CancellationToken cancellationToken)
    {
        if (ensured) return;
        const string sql = """
            CREATE TABLE IF NOT EXISTS "ZaloTrendMusicOverride" (
                "ConnectionId" TEXT NOT NULL,
                "GroupId" TEXT NOT NULL,
                "Period" TEXT NOT NULL,
                "GreetingDisabled" INTEGER NOT NULL DEFAULT 0,
                "GreetingText" TEXT NULL,
                "DisplayTitle" TEXT NULL,
                "DisplayArtist" TEXT NULL,
                "ArtworkUrl" TEXT NULL,
                "ShowLogo" INTEGER NULL,
                "ExpiresAt" TEXT NOT NULL,
                "UpdatedAt" TEXT NOT NULL,
                PRIMARY KEY ("ConnectionId", "GroupId", "Period")
            );
            """;
        await using var command = await CreateCommandAsync(sql, cancellationToken);
        await command.ExecuteNonQueryAsync(cancellationToken);
        ensured = true;
    }

    private async Task<DbCommand> CreateCommandAsync(string sql, CancellationToken cancellationToken)
    {
        var connection = db.Database.GetDbConnection();
        if (connection.State != ConnectionState.Open)
            await connection.OpenAsync(cancellationToken);
        var command = connection.CreateCommand();
        command.CommandText = sql;
        if (db.Database.CurrentTransaction is { } transaction)
            command.Transaction = transaction.GetDbTransaction();
        return command;
    }

    private static void Add(DbCommand command, string name, object? value)
    {
        var parameter = command.CreateParameter();
        parameter.ParameterName = name;
        parameter.Value = value ?? DBNull.Value;
        command.Parameters.Add(parameter);
    }
}

internal sealed class ZaloTrendMusicJobStore(VolleyDraftDbContext db)
{
    private bool ensured;

    public async Task<bool> TryBeginAsync(
        string connectionId,
        string groupId,
        string localDate,
        DateTimeOffset now,
        TimeSpan retryAfter,
        CancellationToken cancellationToken)
    {
        await EnsureAsync(cancellationToken);
        const string sql = """
            INSERT INTO "ZaloTrendMusicRenderJob" (
                "ConnectionId", "GroupId", "LocalDate", "State", "AttemptCount", "LastAttemptAt", "UpdatedAt")
            VALUES (@ConnectionId, @GroupId, @LocalDate, 'rendering', 1, @Now, @Now)
            ON CONFLICT ("ConnectionId", "GroupId", "LocalDate") DO UPDATE SET
                "State" = 'rendering',
                "AttemptCount" = "ZaloTrendMusicRenderJob"."AttemptCount" + 1,
                "LastAttemptAt" = excluded."LastAttemptAt",
                "UpdatedAt" = excluded."UpdatedAt"
            WHERE "ZaloTrendMusicRenderJob"."State" <> 'sent'
              AND "ZaloTrendMusicRenderJob"."LastAttemptAt" <= @RetryBefore;
            """;
        await using var command = await CreateCommandAsync(sql, cancellationToken);
        Add(command, "@ConnectionId", connectionId.Trim());
        Add(command, "@GroupId", groupId.Trim());
        Add(command, "@LocalDate", localDate);
        Add(command, "@Now", now.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture));
        Add(command, "@RetryBefore", now.Subtract(retryAfter).ToUniversalTime().ToString("O", CultureInfo.InvariantCulture));
        return await command.ExecuteNonQueryAsync(cancellationToken) > 0;
    }

    public Task MarkFailedAsync(
        string connectionId,
        string groupId,
        string localDate,
        CancellationToken cancellationToken) =>
        UpdateAsync(connectionId, groupId, localDate, "failed", null, cancellationToken);

    public Task MarkSentAsync(
        string connectionId,
        string groupId,
        string localDate,
        string? providerMessageId,
        CancellationToken cancellationToken) =>
        UpdateAsync(connectionId, groupId, localDate, "sent", providerMessageId, cancellationToken);

    public async Task<bool> EnqueueAsync(
        ZaloProactiveTarget target,
        string localDate,
        string idempotencyKey,
        string candidatesJson,
        CancellationToken cancellationToken)
    {
        await EnsureAsync(cancellationToken);
        var now = DateTimeOffset.UtcNow.ToString("O", CultureInfo.InvariantCulture);
        const string sql = """
            INSERT INTO "ZaloTrendMusicRenderQueue" (
                "IdempotencyKey", "ConnectionId", "AccountId", "GroupId", "LocalDate",
                "CandidatesJson", "State", "AttemptCount", "ClaimedAt", "ClaimToken",
                "CreatedAt", "UpdatedAt")
            VALUES (
                @IdempotencyKey, @ConnectionId, @AccountId, @GroupId, @LocalDate,
                @CandidatesJson, 'pending', 0, NULL, NULL, @Now, @Now)
            ON CONFLICT ("IdempotencyKey") DO NOTHING;
            """;
        await using var command = await CreateCommandAsync(sql, cancellationToken);
        Add(command, "@IdempotencyKey", idempotencyKey);
        Add(command, "@ConnectionId", target.ConnectionId.Trim());
        Add(command, "@AccountId", target.AccountId.Trim());
        Add(command, "@GroupId", target.GroupId.Trim());
        Add(command, "@LocalDate", localDate);
        Add(command, "@CandidatesJson", candidatesJson);
        Add(command, "@Now", now);
        return await command.ExecuteNonQueryAsync(cancellationToken) > 0;
    }

    public async Task<ZaloTrendMusicRenderClaim?> TryClaimQueueAsync(
        DateTimeOffset now,
        TimeSpan staleAfter,
        CancellationToken cancellationToken)
    {
        await EnsureAsync(cancellationToken);
        var claimToken = Guid.NewGuid().ToString("N");
        const string sql = """
            UPDATE "ZaloTrendMusicRenderQueue"
            SET "State" = 'claimed',
                "AttemptCount" = "AttemptCount" + 1,
                "ClaimedAt" = @Now,
                "ClaimToken" = @ClaimToken,
                "UpdatedAt" = @Now
            WHERE "IdempotencyKey" = (
                SELECT "IdempotencyKey"
                FROM "ZaloTrendMusicRenderQueue"
                WHERE "State" = 'pending'
                   OR ("State" = 'claimed' AND "ClaimedAt" <= @StaleBefore)
                   OR ("State" = 'failed'
                       AND "IdempotencyKey" LIKE 'trend-music:request:%'
                       AND "AttemptCount" < 3
                       AND "CreatedAt" >= @FailedRetryAfter)
                ORDER BY
                    CASE WHEN "State" = 'failed' THEN 1 ELSE 0 END,
                    CASE WHEN "State" = 'failed' THEN "CreatedAt" END DESC,
                    "CreatedAt",
                    "IdempotencyKey"
                LIMIT 1
            )
              AND ("State" = 'pending'
                   OR ("State" = 'claimed' AND "ClaimedAt" <= @StaleBefore)
                   OR ("State" = 'failed'
                       AND "IdempotencyKey" LIKE 'trend-music:request:%'
                       AND "AttemptCount" < 3
                       AND "CreatedAt" >= @FailedRetryAfter))
            RETURNING "AccountId", "GroupId", "ConnectionId", "LocalDate",
                      "IdempotencyKey", "CandidatesJson", "ClaimToken";
            """;
        await using var command = await CreateCommandAsync(sql, cancellationToken);
        Add(command, "@Now", now.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture));
        Add(command, "@StaleBefore", now.Subtract(staleAfter).ToUniversalTime().ToString("O", CultureInfo.InvariantCulture));
        Add(command, "@FailedRetryAfter", now.AddHours(-24).ToUniversalTime().ToString("O", CultureInfo.InvariantCulture));
        Add(command, "@ClaimToken", claimToken);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        if (!await reader.ReadAsync(cancellationToken)) return null;
        return new ZaloTrendMusicRenderClaim(
            reader.GetString(0),
            reader.GetString(1),
            reader.GetString(2),
            reader.GetString(3),
            reader.GetString(4),
            reader.GetString(5),
            reader.GetString(6));
    }

    public async Task<bool?> IsCurrentQueueClaimAsync(
        string idempotencyKey,
        string? claimToken,
        CancellationToken cancellationToken)
    {
        await EnsureAsync(cancellationToken);
        const string sql = """
            SELECT "State", "ClaimToken"
            FROM "ZaloTrendMusicRenderQueue"
            WHERE "IdempotencyKey" = @IdempotencyKey
            LIMIT 1;
            """;
        await using var command = await CreateCommandAsync(sql, cancellationToken);
        Add(command, "@IdempotencyKey", idempotencyKey);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        if (!await reader.ReadAsync(cancellationToken)) return null;
        var state = reader.GetString(0);
        var storedToken = reader.IsDBNull(1) ? null : reader.GetString(1);
        return string.Equals(state, "claimed", StringComparison.Ordinal) &&
               !string.IsNullOrWhiteSpace(claimToken) &&
               string.Equals(storedToken, claimToken.Trim(), StringComparison.Ordinal);
    }

    public async Task MarkQueueSentAsync(
        string idempotencyKey,
        string? claimToken,
        CancellationToken cancellationToken)
    {
        await EnsureAsync(cancellationToken);
        const string sql = """
            UPDATE "ZaloTrendMusicRenderQueue"
            SET "State" = 'sent',
                "ClaimToken" = NULL,
                "UpdatedAt" = @UpdatedAt
            WHERE "IdempotencyKey" = @IdempotencyKey
              AND "State" = 'claimed'
              AND "ClaimToken" = @ClaimToken;
            """;
        await using var command = await CreateCommandAsync(sql, cancellationToken);
        Add(command, "@UpdatedAt", DateTimeOffset.UtcNow.ToString("O", CultureInfo.InvariantCulture));
        Add(command, "@IdempotencyKey", idempotencyKey);
        Add(command, "@ClaimToken", claimToken?.Trim());
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    public async Task MarkQueueFailedAsync(
        string idempotencyKey,
        string? claimToken,
        CancellationToken cancellationToken)
    {
        await EnsureAsync(cancellationToken);
        const string sql = """
            UPDATE "ZaloTrendMusicRenderQueue"
            SET "State" = 'failed',
                "ClaimToken" = NULL,
                "UpdatedAt" = @UpdatedAt
            WHERE "IdempotencyKey" = @IdempotencyKey
              AND "State" = 'claimed'
              AND "ClaimToken" = @ClaimToken;
            """;
        await using var command = await CreateCommandAsync(sql, cancellationToken);
        Add(command, "@UpdatedAt", DateTimeOffset.UtcNow.ToString("O", CultureInfo.InvariantCulture));
        Add(command, "@IdempotencyKey", idempotencyKey);
        Add(command, "@ClaimToken", claimToken?.Trim());
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    public async Task<bool> IsArtworkReferencedByActiveQueueAsync(
        string publicPath,
        CancellationToken cancellationToken)
    {
        await EnsureAsync(cancellationToken);
        const string sql = """
            SELECT 1
            FROM "ZaloTrendMusicRenderQueue"
            WHERE "State" IN ('pending', 'claimed')
              AND "CandidatesJson" LIKE @Pattern
            LIMIT 1;
            """;
        await using var command = await CreateCommandAsync(sql, cancellationToken);
        Add(command, "@Pattern", $"%{publicPath}%");
        return await command.ExecuteScalarAsync(cancellationToken) is not null;
    }

    private async Task UpdateAsync(
        string connectionId,
        string groupId,
        string localDate,
        string state,
        string? providerMessageId,
        CancellationToken cancellationToken)
    {
        await EnsureAsync(cancellationToken);
        const string sql = """
            UPDATE "ZaloTrendMusicRenderJob"
            SET "State" = @State,
                "ProviderMessageId" = @ProviderMessageId,
                "UpdatedAt" = @UpdatedAt
            WHERE "ConnectionId" = @ConnectionId
              AND "GroupId" = @GroupId
              AND "LocalDate" = @LocalDate;
            """;
        await using var command = await CreateCommandAsync(sql, cancellationToken);
        Add(command, "@State", state);
        Add(command, "@ProviderMessageId", providerMessageId);
        Add(command, "@UpdatedAt", DateTimeOffset.UtcNow.ToString("O", CultureInfo.InvariantCulture));
        Add(command, "@ConnectionId", connectionId.Trim());
        Add(command, "@GroupId", groupId.Trim());
        Add(command, "@LocalDate", localDate);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private async Task EnsureAsync(CancellationToken cancellationToken)
    {
        if (ensured) return;
        const string sql = """
            CREATE TABLE IF NOT EXISTS "ZaloTrendMusicRenderJob" (
                "ConnectionId" TEXT NOT NULL,
                "GroupId" TEXT NOT NULL,
                "LocalDate" TEXT NOT NULL,
                "State" TEXT NOT NULL,
                "AttemptCount" INTEGER NOT NULL,
                "LastAttemptAt" TEXT NOT NULL,
                "ProviderMessageId" TEXT NULL,
                "UpdatedAt" TEXT NOT NULL,
                PRIMARY KEY ("ConnectionId", "GroupId", "LocalDate")
            );
            CREATE TABLE IF NOT EXISTS "ZaloTrendMusicRenderQueue" (
                "IdempotencyKey" TEXT NOT NULL PRIMARY KEY,
                "ConnectionId" TEXT NOT NULL,
                "AccountId" TEXT NOT NULL,
                "GroupId" TEXT NOT NULL,
                "LocalDate" TEXT NOT NULL,
                "CandidatesJson" TEXT NOT NULL,
                "State" TEXT NOT NULL,
                "AttemptCount" INTEGER NOT NULL,
                "ClaimedAt" TEXT NULL,
                "ClaimToken" TEXT NULL,
                "CreatedAt" TEXT NOT NULL,
                "UpdatedAt" TEXT NOT NULL
            );
            """;
        await using var command = await CreateCommandAsync(sql, cancellationToken);
        await command.ExecuteNonQueryAsync(cancellationToken);
        ensured = true;
    }

    private async Task<DbCommand> CreateCommandAsync(string sql, CancellationToken cancellationToken)
    {
        var connection = db.Database.GetDbConnection();
        if (connection.State != ConnectionState.Open)
            await connection.OpenAsync(cancellationToken);
        var command = connection.CreateCommand();
        command.CommandText = sql;
        if (db.Database.CurrentTransaction is { } transaction)
            command.Transaction = transaction.GetDbTransaction();
        return command;
    }

    private static void Add(DbCommand command, string name, object? value)
    {
        var parameter = command.CreateParameter();
        parameter.ParameterName = name;
        parameter.Value = value ?? DBNull.Value;
        command.Parameters.Add(parameter);
    }
}
