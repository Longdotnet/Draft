using System.Data;
using System.Data.Common;
using System.Globalization;
using System.Text;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using VolleyDraft.Api.Contracts;
using VolleyDraft.Api.Data;

namespace VolleyDraft.Api.Services;

public sealed record ZaloLegacyIdentityEnrichmentResult(
    IReadOnlyList<ZaloIdentityResolution> Resolutions,
    IReadOnlyList<string> AddedZaloUserIds);

/// <summary>
/// Incremental adapter for legacy handlers that already honor structured mention UIDs.
/// It detects exact member display names / user-approved aliases in an addressed bot
/// command, resolves each phrase through ZaloIdentityResolver, and promotes only a
/// unique resolved UID to a metadata-only mention. Ambiguous phrases never guess.
/// </summary>
public sealed class ZaloLegacyIdentityMigrationAdapter(VolleyDraftDbContext db)
{
    public async Task<ZaloLegacyIdentityEnrichmentResult> EnrichAsync(
        string groupId,
        ZaloIncomingMessageEvent incoming,
        CancellationToken cancellationToken = default)
    {
        groupId = Clean(groupId, 100);
        var question = Normalize(incoming.Content);
        if (groupId.Length == 0 || question.Length == 0)
            return new([], []);

        var memberRows = await db.ZaloGroupMembers.AsNoTracking()
            .Where(item => item.GroupId == groupId && item.IsCurrentMember)
            .Select(item => new { item.ZaloUserId, item.DisplayName })
            .ToListAsync(cancellationToken);
        if (memberRows.Count == 0) return new([], []);

        var phrases = new HashSet<string>(StringComparer.Ordinal);
        foreach (var member in memberRows)
        {
            var display = Normalize(member.DisplayName);
            if (IsSafeReference(display) && ContainsWholePhrase(question, display)) phrases.Add(display);
        }

        var senderId = Clean(incoming.SenderId, 100);
        if (senderId.Length > 0)
        {
            // Ensure the additive raw-SQL concept schema exists before the single
            // batch alias query below. This avoids N+1 concept reads per member.
            _ = await new ZaloUserConceptStore(db).LoadActiveAsync(groupId, senderId, 1, cancellationToken);
            foreach (var alias in await LoadActiveAliasesAsync(groupId, cancellationToken))
            {
                var normalizedAlias = Normalize(alias);
                if (IsSafeReference(normalizedAlias) && ContainsWholePhrase(question, normalizedAlias))
                    phrases.Add(normalizedAlias);
            }
        }

        if (phrases.Count == 0) return new([], []);

        var quote = ZaloQuotedContextResolver.Resolve(incoming, incoming.Content);
        var resolver = new ZaloIdentityResolver(db);
        var resolutions = new List<ZaloIdentityResolution>();
        foreach (var phrase in phrases.OrderByDescending(item => item.Length))
        {
            cancellationToken.ThrowIfCancellationRequested();
            resolutions.Add(await resolver.ResolveAsync(
                groupId,
                phrase,
                currentSenderZaloUserId: senderId,
                quotedContext: quote,
                cancellationToken: cancellationToken));
        }

        if (incoming.Mentions is not List<ZaloBridgeMention> mutableMentions)
            return new(resolutions, []);

        var existingIds = mutableMentions
            .Select(item => Clean(item.Uid, 100))
            .Where(item => item.Length > 0)
            .ToHashSet(StringComparer.Ordinal);
        var botId = Clean(incoming.BotId, 100);
        var added = new List<string>();
        foreach (var resolution in resolutions
                     .Where(item => item.Status == ZaloIdentityResolutionStatus.Resolved && !string.IsNullOrWhiteSpace(item.ZaloUserId))
                     .OrderByDescending(item => item.Confidence))
        {
            var uid = Clean(resolution.ZaloUserId, 100);
            if (uid.Length == 0 || uid == botId || !existingIds.Add(uid)) continue;
            mutableMentions.Add(new ZaloBridgeMention(uid, -1, 0));
            added.Add(uid);
        }

        return new(resolutions, added);
    }

    private async Task<IReadOnlyList<string>> LoadActiveAliasesAsync(
        string groupId,
        CancellationToken cancellationToken)
    {
        var connection = db.Database.GetDbConnection();
        if (connection.State != ConnectionState.Open) await connection.OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT "ValueJson", "ExpiresAt"
            FROM "ZaloUserConcepts"
            WHERE "GroupId" = @groupId
              AND "ConceptType" = 'Alias'
              AND "ConceptKey" = 'preferred_name'
              AND "Status" = 'Active';
            """;
        Add(command, "@groupId", groupId);
        var now = DateTimeOffset.UtcNow;
        var result = new List<string>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            if (!reader.IsDBNull(1) && Timestamp(reader.GetValue(1)) <= now) continue;
            var valueJson = reader.IsDBNull(0) ? null : Convert.ToString(reader.GetValue(0));
            var alias = ReadAlias(valueJson);
            if (!string.IsNullOrWhiteSpace(alias)) result.Add(alias!);
        }
        return result;
    }

    private static string? ReadAlias(string? valueJson)
    {
        try
        {
            using var document = JsonDocument.Parse(valueJson ?? "{}");
            return document.RootElement.TryGetProperty("name", out var name) && name.ValueKind == JsonValueKind.String
                ? name.GetString()?.Trim()
                : null;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static bool ContainsWholePhrase(string normalizedQuestion, string normalizedPhrase) =>
        $" {normalizedQuestion} ".Contains($" {normalizedPhrase} ", StringComparison.Ordinal);

    private static bool IsSafeReference(string value) =>
        value.Length >= 2 && value is not ("tui" or "toi" or "minh" or "em" or "anh" or "chi" or "ban" or "bot");

    private static string Normalize(string? value)
    {
        var decomposed = (value ?? string.Empty).Trim().ToLowerInvariant().Normalize(NormalizationForm.FormD);
        var builder = new StringBuilder(decomposed.Length);
        foreach (var character in decomposed)
        {
            if (CharUnicodeInfo.GetUnicodeCategory(character) == UnicodeCategory.NonSpacingMark) continue;
            builder.Append(character == 'đ' ? 'd' : char.IsLetterOrDigit(character) ? character : ' ');
        }
        return string.Join(' ', builder.ToString().Normalize(NormalizationForm.FormC)
            .Split(' ', StringSplitOptions.RemoveEmptyEntries));
    }

    private static string Clean(string? value, int maxLength)
    {
        var text = (value ?? string.Empty).Trim();
        return text.Length <= maxLength ? text : text[..maxLength];
    }

    private static DateTimeOffset Timestamp(object value)
    {
        if (value is DateTimeOffset dto) return dto;
        if (value is DateTime dt) return new DateTimeOffset(DateTime.SpecifyKind(dt, DateTimeKind.Utc));
        return DateTimeOffset.TryParse(Convert.ToString(value), out var parsed) ? parsed : DateTimeOffset.UnixEpoch;
    }

    private static void Add(DbCommand command, string name, object value)
    {
        var parameter = command.CreateParameter();
        parameter.ParameterName = name;
        parameter.Value = value;
        command.Parameters.Add(parameter);
    }
}
