using System.Text.Json;
using VolleyDraft.Api.Contracts;
using VolleyDraft.Api.Data;

namespace VolleyDraft.Api.Services;

public enum ZaloMemoryCommandKind
{
    List,
    ForgetKey,
    ForgetAll
}

public sealed record ZaloMemoryCommand(ZaloMemoryCommandKind Kind, string? ConceptKey = null);

public sealed record ZaloMemoryPreRouteResult(
    bool Handled,
    string? Response,
    ZaloUserConceptSnapshot? RememberedConcept,
    ZaloMemoryCommand? Command);

/// <summary>
/// Deterministic user-facing memory controls. AI may help phrase an answer later,
/// but it is never allowed to decide what persistent memory is deleted.
/// </summary>
public sealed class ZaloMemoryV2Service(VolleyDraftDbContext db)
{
    public async Task<ZaloMemoryPreRouteResult> ProcessAsync(
        string groupId,
        ZaloIncomingMessageEvent incoming,
        string question,
        CancellationToken cancellationToken = default)
    {
        // Bind quote context to the current ASP.NET request Activity before any
        // downstream awaits; classifier/answer assembly can read the same turn later.
        ZaloTurnQuoteContext.Set(incoming);

        var sender = new ZaloAiSender(Clean(incoming.SenderId, 100), Clean(incoming.SenderName, 160));
        if (sender.Id.Length == 0 || string.IsNullOrWhiteSpace(groupId))
            return new(false, null, null, null);

        // Transitional identity migration: legacy handlers already understand
        // structured mention UIDs. Promote only uniquely resolved exact member
        // names / approved aliases to metadata-only mentions before routing.
        await TryEnrichLegacyIdentityAsync(groupId, incoming, cancellationToken);

        // An ambient proposal stays read-only until the requester replies to the
        // exact provider message that presented the latest ready proposal. When that
        // deterministic confirmation is present, promote only a short-lived legacy
        // confirmation envelope; return-path remains unhandled so the same inbound
        // webhook continues into the existing atomic ZaloBotService apply path.
        try
        {
            await new ZaloAmbientTeamPreferenceHandoff(db)
                .TryPromoteExactReplyConfirmationAsync(incoming, cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch
        {
            // Proposal handoff is additive. A malformed/stale proposal or graph
            // lookup problem must never block the explicit legacy router.
        }

        var store = new ZaloUserConceptStore(db);
        if (TryParseCommand(question, out var command))
        {
            var response = await ExecuteCommandAsync(groupId, sender.Id, store, command, cancellationToken);
            return new(true, response, null, command);
        }

        if (!ZaloUserConceptExtractor.TryExtract(question, sender, out var draft))
            return new(false, null, null, null);

        var concept = await store.RememberAsync(
            groupId,
            sender,
            draft,
            incoming.MessageId,
            cancellationToken);
        return new(false, null, concept, null);
    }

    public static bool TryParseCommand(string? text, out ZaloMemoryCommand command)
    {
        command = null!;
        var value = ZaloBotIntelligence.Normalize(text ?? string.Empty).Trim();
        if (value.Length == 0) return false;
        value = StripBotPrefix(value);

        // Destructive commands must be recognized before broad phrases such as
        // "memory cua tui"; otherwise "xoa het memory cua tui" can be mistaken
        // for a list request merely because it contains that substring.
        if (HasAny(value,
                "xoa het memory", "xoa toan bo memory", "quen het ve tui", "quen het ve toi",
                "xoa het ky uc", "xoa tat ca ky uc", "xoa moi thu nho ve tui", "xoa moi thu nho ve toi"))
        {
            command = new(ZaloMemoryCommandKind.ForgetAll);
            return true;
        }

        if (HasAny(value,
                "nho gi ve tui", "nho gi ve toi", "nho gi ve minh", "nho gi ve em",
                "memory cua tui", "memory cua toi", "ky uc cua tui", "ky uc cua toi",
                "xem memory", "xem ky uc"))
        {
            command = new(ZaloMemoryCommandKind.List);
            return true;
        }

        if (!HasAny(value, "quen", "xoa memory", "xoa ky uc", "dung nho", "khong nho")) return false;
        var key = HasAny(value, "ten goi", "biet danh", "goi tui", "goi toi", "alias", "ten cua tui", "ten cua toi")
            ? "preferred_name"
            : HasAny(value, "lich choi", "lich danh", "ngay choi", "ngay danh", "t2", "t3", "t4", "t5", "t6", "t7", "chu nhat", "cn")
                ? "session_availability"
                : HasAny(value, "vi tri", "libero", "setter", "chuyen 2", "phu cong", "chu cong", "doi chuyen")
                    ? "volleyball_role"
                    : null;
        if (key is null) return false;
        command = new(ZaloMemoryCommandKind.ForgetKey, key);
        return true;
    }

    private async Task TryEnrichLegacyIdentityAsync(
        string groupId,
        ZaloIncomingMessageEvent incoming,
        CancellationToken cancellationToken)
    {
        try
        {
            var enrichment = await new ZaloLegacyIdentityMigrationAdapter(db)
                .EnrichAsync(groupId, incoming, cancellationToken);
            if (enrichment.AddedZaloUserIds.Count == 0) return;

            var addedIds = enrichment.AddedZaloUserIds.ToHashSet(StringComparer.Ordinal);
            var personKeys = enrichment.Resolutions
                .Where(item => item.Status == ZaloIdentityResolutionStatus.Resolved &&
                               item.ZaloUserId is not null &&
                               addedIds.Contains(item.ZaloUserId) &&
                               item.PersonKey is not null)
                .Select(item => item.PersonKey!)
                .Distinct(StringComparer.Ordinal)
                .ToArray();
            var quote = ZaloQuotedContextResolver.Resolve(incoming, incoming.Content);
            await new ZaloBotTraceStore(db).WriteAsync(
                new ZaloBotTraceEntry(
                    incoming.MessageId,
                    groupId,
                    Clean(incoming.SenderId, 100),
                    quote.RepliesToBot ? "ReplyToBot" : "ExplicitMention",
                    IntentSource: "IdentityPreRouting",
                    Confidence: 1,
                    QuotedMessageId: quote.MessageId,
                    ResolvedPersonIdsJson: JsonSerializer.Serialize(personKeys),
                    AiCalled: false),
                cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch
        {
            // Identity enrichment is a migration aid. Never block the existing
            // deterministic/domain router if this additive path is unavailable.
        }
    }

    private async Task<string> ExecuteCommandAsync(
        string groupId,
        string senderId,
        ZaloUserConceptStore store,
        ZaloMemoryCommand command,
        CancellationToken cancellationToken)
    {
        var active = await store.LoadActiveAsync(groupId, senderId, 50, cancellationToken);
        if (command.Kind == ZaloMemoryCommandKind.List)
        {
            if (active.Count == 0) return "Mình hiện không lưu memory cá nhân nào về bạn trong nhóm này.";
            var lines = active
                .OrderBy(item => item.ConceptType, StringComparer.Ordinal)
                .ThenBy(item => item.Key, StringComparer.Ordinal)
                .Select((item, index) => $"{index + 1}. {Describe(item)}")
                .ToList();
            return "Mình đang nhớ các thông tin bạn đã tự nói trong nhóm này:\n" + string.Join("\n", lines) +
                   "\nBạn có thể bảo mình quên tên gọi, lịch chơi, vị trí hoặc xóa hết memory.";
        }

        var privacy = new ZaloUserConceptPrivacyStore(db);
        if (command.Kind == ZaloMemoryCommandKind.ForgetAll)
        {
            var deleted = await privacy.DeleteAllAsync(groupId, senderId, cancellationToken);
            return deleted == 0
                ? "Mình không có memory cá nhân nào để xóa trong nhóm này."
                : $"Đã xóa toàn bộ {deleted} bản ghi memory cá nhân của bạn trong nhóm này, gồm cả lịch sử cũ đã bị thay thế.";
        }

        var count = await privacy.DeleteKeyHistoryAsync(
            groupId,
            senderId,
            command.ConceptKey ?? string.Empty,
            cancellationToken);
        return count == 0
            ? "Mình không tìm thấy memory đó để xóa."
            : $"Đã quên {DescribeKey(command.ConceptKey)} của bạn trong nhóm này và xóa cả lịch sử cũ của mục đó.";
    }

    private static string Describe(ZaloUserConceptSnapshot concept)
    {
        try
        {
            using var document = JsonDocument.Parse(concept.ValueJson);
            var root = document.RootElement;
            return concept.Key switch
            {
                "preferred_name" when root.TryGetProperty("name", out var name) => $"Tên muốn được gọi: {name.GetString()}",
                "volleyball_role" when root.TryGetProperty("role", out var role) => $"Vị trí bóng chuyền: {role.GetString()}",
                "session_availability" => DescribeSessionPreference(root),
                _ => $"{concept.ConceptType}/{concept.Key}"
            };
        }
        catch (JsonException)
        {
            return $"{concept.ConceptType}/{concept.Key}";
        }
    }

    private static string DescribeSessionPreference(JsonElement root)
    {
        var sessions = root.TryGetProperty("sessions", out var sessionNode) && sessionNode.ValueKind == JsonValueKind.Array
            ? string.Join(", ", sessionNode.EnumerateArray().Select(item => item.GetString()).Where(item => !string.IsNullOrWhiteSpace(item)))
            : "chưa rõ";
        var mode = root.TryGetProperty("mode", out var modeNode) ? modeNode.GetString() : null;
        var label = mode switch
        {
            "avoid" => "thường tránh",
            "only" => "chỉ chơi",
            "available" => "có thể chơi",
            _ => "thường ưu tiên"
        };
        return $"Lịch chơi: {label} {sessions}";
    }

    private static string DescribeKey(string? key) => key switch
    {
        "preferred_name" => "tên gọi",
        "session_availability" => "lịch chơi",
        "volleyball_role" => "vị trí bóng chuyền",
        _ => "memory này"
    };

    private static string StripBotPrefix(string value)
    {
        foreach (var prefix in new[] { "@bot ", "bot ", "@npc ", "npc " })
            if (value.StartsWith(prefix, StringComparison.Ordinal)) return value[prefix.Length..].Trim();
        return value;
    }

    private static bool HasAny(string value, params string[] tokens) =>
        tokens.Any(token => value.Contains(token, StringComparison.Ordinal));

    private static string Clean(string? value, int maxLength)
    {
        var text = (value ?? string.Empty).Trim();
        return text.Length <= maxLength ? text : text[..maxLength];
    }
}
