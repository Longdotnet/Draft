using System.Text;
using System.Text.RegularExpressions;
using VolleyDraft.Api.Contracts;

namespace VolleyDraft.Api.Services;

public sealed record ZaloTeamMentionPlayer(string DisplayName, string? ZaloUserId);

public sealed record ZaloTeamLineupMessage(
    string Text,
    IReadOnlyList<BridgeOutgoingMention> Mentions);

public static class ZaloTeamLineupFormatter
{
    public static bool WantsPlayerMentions(string question)
    {
        var normalized = ZaloBotIntelligence.Normalize(question);
        return Regex.IsMatch(
            normalized,
            @"(?:tag|mention)\s+(?:(?:tung|moi|het|tat\s+ca|toan\s+bo|cac)\s+)?(?:nguoi|thanh\s+vien|player|nguoi\s+choi)|(?:tag|mention)\s+(?:ca|het|tung)\s+(?:team|doi)|(?:tag|mention)\s+3\s+team",
            RegexOptions.CultureInvariant);
    }

    public static ZaloTeamLineupMessage Format(
        string sessionName,
        IReadOnlyList<TeamPreviewResponse> teams,
        IReadOnlyDictionary<string, IReadOnlyList<ZaloTeamMentionPlayer>>? playersBySlot = null)
    {
        if (teams.Count == 0 || teams.All(team => team.Slots.Count == 0))
        {
            return new ZaloTeamLineupMessage(
                $"{sessionName} chưa có kết quả chia team. Dùng lệnh 9 nếu bạn là operator và muốn tự chạy draft.",
                []);
        }

        var builder = new StringBuilder($"Đội hình {sessionName}:");
        var mentions = new List<BridgeOutgoingMention>();
        foreach (var team in teams.Take(3))
        {
            builder.Append('\n').Append(team.TeamName).Append(" (");
            builder.Append(string.IsNullOrWhiteSpace(team.CaptainName)
                ? "chưa chọn đội trưởng"
                : $"đội trưởng {team.CaptainName}");
            builder.Append("): ");
            if (team.Slots.Count == 0)
            {
                builder.Append("chưa có thành viên");
                continue;
            }

            for (var slotIndex = 0; slotIndex < team.Slots.Count; slotIndex += 1)
            {
                if (slotIndex > 0) builder.Append(", ");
                var slot = team.Slots[slotIndex];
                if (playersBySlot is null ||
                    !playersBySlot.TryGetValue(slot.Id, out var slotPlayers) ||
                    slotPlayers.Count == 0)
                {
                    builder.Append(slot.DisplayName);
                    continue;
                }

                for (var playerIndex = 0; playerIndex < slotPlayers.Count; playerIndex += 1)
                {
                    if (playerIndex > 0) builder.Append(" / ");
                    var player = slotPlayers[playerIndex];
                    var displayName = player.DisplayName.Trim().TrimStart('@');
                    if (string.IsNullOrWhiteSpace(player.ZaloUserId))
                    {
                        builder.Append(displayName);
                        continue;
                    }

                    var label = $"@{displayName}";
                    var position = builder.Length;
                    builder.Append(label);
                    mentions.Add(new BridgeOutgoingMention(player.ZaloUserId.Trim(), position, label.Length));
                }
            }
        }

        return new ZaloTeamLineupMessage(builder.ToString(), mentions);
    }
}
