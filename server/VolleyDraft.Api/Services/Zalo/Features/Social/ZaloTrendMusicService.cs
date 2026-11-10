using System.Data;
using System.Data.Common;
using System.Globalization;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
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
    string? LicenseLabel = null);

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

internal sealed record ZaloTrendMusicSettings(
    bool Enabled,
    string FeedUrl,
    string JamendoClientId,
    string GitHubToken,
    string GitHubRepository,
    string GitHubWorkflow,
    string GitHubRef,
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
    ZaloIntegrationService integration,
    IConfiguration configuration,
    ILogger<ZaloTrendMusicService> logger)
{
    private static readonly TimeSpan VietnamOffset = TimeSpan.FromHours(7);
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        PropertyNameCaseInsensitive = true
    };
    private readonly ZaloTrendMusicJobStore jobs = new(db);
    private readonly ZaloProactiveMessageStore proactiveStore = new(db);

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
                session.Status != SessionStatus.Cancelled)
            .OrderByDescending(session => session.StartTime)
            .ThenByDescending(session => session.UpdatedAt)
            .Take(10)
            .ToListAsync(cancellationToken);

        var authorized = sessions.Any(session =>
            ZaloOperatorPermissionCommandService.ParseOperatorIds(session.BotOperatorZaloUserIdsJson)
                .Contains(senderId));
        if (!authorized && sessions.Count > 0)
        {
            var role = await integration.GetGroupRoleAuthorizationAsync(
                sessions[0].AdminUserId,
                sessions[0].Id,
                senderId);
            authorized = role.IsSuccess && role.Value?.CanOperateBot == true;
        }

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
            logger.LogWarning(exception, "Trend music manual test source unavailable; using original fallback");
            candidates = [];
        }

        var localDate = DateOnly.FromDateTime(DateTimeOffset.UtcNow.ToOffset(VietnamOffset).Date)
            .ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
        var messageId = SanitizeId(incoming.MessageId);
        if (messageId.Length == 0) messageId = Guid.NewGuid().ToString("N");
        var idempotencyKey = $"trend-music:test:{target.ConnectionId}:{target.GroupId}:{messageId}";

        try
        {
            var queued = await DispatchOrQueueRenderAsync(
                settings,
                target,
                localDate,
                idempotencyKey,
                candidates,
                cancellationToken);
            await SendTestReplyAsync(
                incoming,
                queued
                    ? candidates.Count > 0
                        ? $"🎵 Đã xếp bản test {candidates[0].Title} — {candidates[0].Artist}. GitHub worker quét mỗi 5 phút; render xong bot gửi video ngay trong nhóm này."
                        : "🎵 Đã xếp NPC ORIGINAL fallback để test luồng video. GitHub worker quét mỗi 5 phút rồi gửi video lại nhóm này."
                    : candidates.Count > 0
                        ? $"🎵 Đang dựng bản test từ {candidates[0].Title} — {candidates[0].Artist}. Render xong bot sẽ gửi video ngay trong nhóm này."
                        : "🎵 Đang dựng NPC ORIGINAL fallback để test luồng video.",
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

    public async Task ReconcileAsync(CancellationToken cancellationToken = default)
    {
        var settings = ZaloTrendMusicSettings.FromConfiguration(configuration);
        if (!settings.IsConfigured || !TrendMusicInternalAuthorization.IsConfigured(configuration)) return;

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
            // the deterministic branded NPC-original fallback.
            logger.LogWarning(exception, "Trend music feed unavailable; dispatching original fallback");
            candidates = [];
        }
        if (candidates.Count == 0)
            logger.LogWarning("Trend music feed has no usable candidates; dispatching original fallback");

        var targets = await new ZaloProactiveTargetResolver(db).GetTargetsAsync(cancellationToken);
        var localDate = DateOnly.FromDateTime(local.Date).ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
        foreach (var target in targets.Take(100))
        {
            if (!await jobs.TryBeginAsync(
                    target.ConnectionId,
                    target.GroupId,
                    localDate,
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
                    candidates,
                    cancellationToken);
                logger.LogInformation(
                    "Trend music render scheduled Group={GroupId} Date={LocalDate} Candidates={CandidateCount}",
                    target.GroupId,
                    localDate,
                    candidates.Count);
            }
            catch (Exception exception) when (exception is not OperationCanceledException)
            {
                await jobs.MarkFailedAsync(target.ConnectionId, target.GroupId, localDate, cancellationToken);
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
        if (existing.Any(item =>
                string.Equals(item.Lane, ZaloProactiveLane.TrendMusic, StringComparison.Ordinal) &&
                string.Equals(item.IdempotencyKey, request.IdempotencyKey, StringComparison.Ordinal)))
        {
            await jobs.MarkQueueSentAsync(request.IdempotencyKey, cancellationToken);
            return true;
        }

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
            await proactiveStore.RecordAsync(new ZaloProactiveMessageHistoryData(
                Guid.NewGuid().ToString("N"),
                target.ConnectionId,
                target.GroupId,
                request.LocalDate,
                ZaloProactiveLane.TrendMusic,
                "MorningVideo",
                $"song:{SanitizeId(request.CandidateId)}",
                null,
                request.Artist,
                caption,
                now,
                response.MessageId,
                request.IdempotencyKey), cancellationToken);
            var isManualTest = request.IdempotencyKey.StartsWith("trend-music:test:", StringComparison.Ordinal);
            await proactiveStore.CommitCooldownAsync(
                target.ConnectionId,
                target.GroupId,
                request.IdempotencyKey,
                now.AddMinutes(isManualTest ? 2 : 60),
                cancellationToken);
            if (!isManualTest)
            {
                await jobs.MarkSentAsync(
                    target.ConnectionId,
                    target.GroupId,
                    request.LocalDate,
                    response.MessageId,
                    cancellationToken);
            }
            await jobs.MarkQueueSentAsync(request.IdempotencyKey, cancellationToken);
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
            if (candidates.Count > 0) return candidates;
        }
        return [];
    }

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
        IReadOnlyList<ZaloTrendMusicCandidate> candidates,
        CancellationToken cancellationToken)
    {
        if (!settings.CanDirectDispatch)
        {
            await jobs.EnqueueAsync(
                target,
                localDate,
                idempotencyKey,
                JsonSerializer.Serialize(candidates, JsonOptions),
                cancellationToken);
            return true;
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
            JsonSerializer.Serialize(candidates, JsonOptions),
            cancellationToken);
        return false;
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
        var rank = request.Rank > 0 ? $" • #{request.Rank}" : string.Empty;
        var caption = $"🎵 NPC PICK{rank}\n{request.Title.Trim()} — {request.Artist.Trim()}";
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
            idempotencyKey: $"trend-music:test-reply:{SanitizeId(incoming.MessageId)}:{Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(message)))[..12]}");
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
    string CandidatesJson);

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

    public async Task EnqueueAsync(
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
        await command.ExecuteNonQueryAsync(cancellationToken);
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
                ORDER BY "CreatedAt", "IdempotencyKey"
                LIMIT 1
            )
              AND ("State" = 'pending'
                   OR ("State" = 'claimed' AND "ClaimedAt" <= @StaleBefore))
            RETURNING "AccountId", "GroupId", "ConnectionId", "LocalDate",
                      "IdempotencyKey", "CandidatesJson";
            """;
        await using var command = await CreateCommandAsync(sql, cancellationToken);
        Add(command, "@Now", now.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture));
        Add(command, "@StaleBefore", now.Subtract(staleAfter).ToUniversalTime().ToString("O", CultureInfo.InvariantCulture));
        Add(command, "@ClaimToken", claimToken);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        if (!await reader.ReadAsync(cancellationToken)) return null;
        return new ZaloTrendMusicRenderClaim(
            reader.GetString(0),
            reader.GetString(1),
            reader.GetString(2),
            reader.GetString(3),
            reader.GetString(4),
            reader.GetString(5));
    }

    public async Task MarkQueueSentAsync(
        string idempotencyKey,
        CancellationToken cancellationToken)
    {
        await EnsureAsync(cancellationToken);
        const string sql = """
            UPDATE "ZaloTrendMusicRenderQueue"
            SET "State" = 'sent',
                "ClaimToken" = NULL,
                "UpdatedAt" = @UpdatedAt
            WHERE "IdempotencyKey" = @IdempotencyKey;
            """;
        await using var command = await CreateCommandAsync(sql, cancellationToken);
        Add(command, "@UpdatedAt", DateTimeOffset.UtcNow.ToString("O", CultureInfo.InvariantCulture));
        Add(command, "@IdempotencyKey", idempotencyKey);
        await command.ExecuteNonQueryAsync(cancellationToken);
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
