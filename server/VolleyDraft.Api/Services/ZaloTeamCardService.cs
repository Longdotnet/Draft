using System.Buffers.Binary;
using System.Globalization;
using System.IO.Compression;
using System.Text;
using Microsoft.EntityFrameworkCore;
using VolleyDraft.Api.Data;

namespace VolleyDraft.Api.Services;

public sealed class ZaloTeamCardService(VolleyDraftDbContext db, IConfiguration configuration)
{
    public async Task<GeneratedTeamCard?> GenerateAsync(string sessionId, CancellationToken cancellationToken = default)
    {
        var session = await db.MatchSessions.AsNoTracking().SingleOrDefaultAsync(item => item.Id == sessionId, cancellationToken);
        if (session is null) return null;
        var teams = await db.Teams.AsNoTracking()
            .Include(team => team.CaptainSessionPlayer)
            .Include(team => team.AssignedSlots)
            .Where(team => team.SessionId == sessionId)
            .OrderBy(team => team.Name)
            .Select(team => new TeamCardTeam(
                team.Name,
                team.CaptainSessionPlayer == null ? null : team.CaptainSessionPlayer.DisplayName,
                team.AssignedSlots.OrderByDescending(slot => slot.IsCaptainSlot).ThenBy(slot => slot.DisplayName)
                    .Select(slot => slot.DisplayName).ToList()))
            .ToListAsync(cancellationToken);
        return new GeneratedTeamCard(SimpleTeamCardPng.Render(session.Name, session.StartTime, teams), "image/png");
    }

    public string GetPublicUrl(string sessionId)
    {
        var configured = configuration["Public:BaseUrl"]?.TrimEnd('/');
        if (string.IsNullOrWhiteSpace(configured) && Uri.TryCreate(configuration["Zalo:WebhookUrl"], UriKind.Absolute, out var webhook))
            configured = webhook.GetLeftPart(UriPartial.Authority);
        configured ??= "http://localhost:5030";
        return $"{configured}/api/public/sessions/{Uri.EscapeDataString(sessionId)}/team-card.png?v={DateTimeOffset.UtcNow.ToUnixTimeSeconds()}";
    }
}

public sealed record GeneratedTeamCard(byte[] Data, string ContentType);
public sealed record TeamCardTeam(string Name, string? CaptainName, IReadOnlyList<string> Players);

internal static class SimpleTeamCardPng
{
    private const int Width = 1200;
    private const int Height = 760;
    private static readonly Dictionary<char, string[]> Font = BuildFont();

    public static byte[] Render(string sessionName, DateTimeOffset? startTime, IReadOnlyList<TeamCardTeam> teams)
    {
        var canvas = new Canvas(Width, Height, new Rgb(245, 247, 251));
        canvas.FillRect(0, 0, Width, 120, new Rgb(15, 23, 42));
        canvas.DrawText("VOLLEY DRAFT", 48, 30, 6, new Rgb(56, 189, 248));
        canvas.DrawText(ToAscii(sessionName), 48, 76, 3, new Rgb(255, 255, 255));
        if (startTime is not null)
        {
            var local = startTime.Value.ToOffset(TimeSpan.FromHours(7));
            canvas.DrawText(local.ToString("dd/MM/yyyy HH:mm", CultureInfo.InvariantCulture), 850, 78, 3, new Rgb(203, 213, 225));
        }

        var colors = new[] { new Rgb(14, 165, 233), new Rgb(249, 115, 22), new Rgb(34, 197, 94) };
        var cards = teams.Take(3).ToList();
        if (cards.Count == 0)
        {
            canvas.DrawText("CHUA CO KET QUA CHIA TEAM", 250, 350, 5, new Rgb(100, 116, 139));
            return canvas.EncodePng();
        }

        const int gap = 24;
        const int margin = 30;
        var cardWidth = (Width - margin * 2 - gap * 2) / 3;
        for (var index = 0; index < cards.Count; index += 1)
        {
            var team = cards[index];
            var x = margin + index * (cardWidth + gap);
            canvas.FillRect(x, 145, cardWidth, 580, new Rgb(255, 255, 255));
            canvas.FillRect(x, 145, cardWidth, 72, colors[index]);
            canvas.DrawText(ToAscii(team.Name), x + 24, 170, 4, new Rgb(255, 255, 255), cardWidth - 48);
            var captain = string.IsNullOrWhiteSpace(team.CaptainName) ? "CAPTAIN: CHUA CHON" : $"CAPTAIN: {ToAscii(team.CaptainName)}";
            canvas.DrawText(captain, x + 24, 238, 2, new Rgb(71, 85, 105), cardWidth - 48);
            var players = team.Players.Count == 0 ? ["CHUA CO THANH VIEN"] : team.Players;
            for (var playerIndex = 0; playerIndex < players.Count && playerIndex < 11; playerIndex += 1)
            {
                var label = $"{playerIndex + 1}. {ToAscii(players[playerIndex])}";
                canvas.DrawText(label, x + 24, 285 + playerIndex * 38, 2, new Rgb(15, 23, 42), cardWidth - 48);
            }
            canvas.DrawText($"TOTAL: {team.Players.Count}", x + 24, 684, 2, colors[index]);
        }
        return canvas.EncodePng();
    }

    private static string ToAscii(string value)
    {
        var decomposed = value.ToUpperInvariant().Normalize(NormalizationForm.FormD);
        var builder = new StringBuilder();
        foreach (var character in decomposed)
        {
            if (CharUnicodeInfo.GetUnicodeCategory(character) == UnicodeCategory.NonSpacingMark) continue;
            builder.Append(character == 'Đ' ? 'D' : character);
        }
        return builder.ToString().Normalize(NormalizationForm.FormC);
    }

    private sealed class Canvas
    {
        private readonly int width;
        private readonly int height;
        private readonly byte[] pixels;

        public Canvas(int width, int height, Rgb background)
        {
            this.width = width;
            this.height = height;
            pixels = new byte[width * height * 4];
            FillRect(0, 0, width, height, background);
        }

        public void FillRect(int x, int y, int rectWidth, int rectHeight, Rgb color)
        {
            for (var py = Math.Max(0, y); py < Math.Min(height, y + rectHeight); py += 1)
            for (var px = Math.Max(0, x); px < Math.Min(width, x + rectWidth); px += 1)
                SetPixel(px, py, color);
        }

        public void DrawText(string text, int x, int y, int scale, Rgb color, int maxWidth = int.MaxValue)
        {
            var cursor = x;
            foreach (var character in text)
            {
                if (cursor + 6 * scale > x + maxWidth) break;
                if (!Font.TryGetValue(character, out var glyph)) glyph = Font['?'];
                for (var row = 0; row < glyph.Length; row += 1)
                for (var column = 0; column < glyph[row].Length; column += 1)
                    if (glyph[row][column] == '1') FillRect(cursor + column * scale, y + row * scale, scale, scale, color);
                cursor += 6 * scale;
            }
        }

        private void SetPixel(int x, int y, Rgb color)
        {
            var offset = (y * width + x) * 4;
            pixels[offset] = color.R;
            pixels[offset + 1] = color.G;
            pixels[offset + 2] = color.B;
            pixels[offset + 3] = 255;
        }

        public byte[] EncodePng()
        {
            using var output = new MemoryStream();
            output.Write([137, 80, 78, 71, 13, 10, 26, 10]);
            var header = new byte[13];
            BinaryPrimitives.WriteInt32BigEndian(header.AsSpan(0, 4), width);
            BinaryPrimitives.WriteInt32BigEndian(header.AsSpan(4, 4), height);
            header[8] = 8;
            header[9] = 6;
            WriteChunk(output, "IHDR", header);
            using var raw = new MemoryStream();
            for (var row = 0; row < height; row += 1)
            {
                raw.WriteByte(0);
                raw.Write(pixels, row * width * 4, width * 4);
            }
            raw.Position = 0;
            using var compressed = new MemoryStream();
            using (var zlib = new ZLibStream(compressed, CompressionLevel.Fastest, true)) raw.CopyTo(zlib);
            WriteChunk(output, "IDAT", compressed.ToArray());
            WriteChunk(output, "IEND", []);
            return output.ToArray();
        }
    }

    private static void WriteChunk(Stream stream, string type, byte[] data)
    {
        Span<byte> length = stackalloc byte[4];
        BinaryPrimitives.WriteInt32BigEndian(length, data.Length);
        stream.Write(length);
        var typeBytes = Encoding.ASCII.GetBytes(type);
        stream.Write(typeBytes);
        stream.Write(data);
        var crcInput = new byte[typeBytes.Length + data.Length];
        typeBytes.CopyTo(crcInput, 0);
        data.CopyTo(crcInput, typeBytes.Length);
        Span<byte> crc = stackalloc byte[4];
        BinaryPrimitives.WriteUInt32BigEndian(crc, Crc32(crcInput));
        stream.Write(crc);
    }

    private static uint Crc32(byte[] data)
    {
        var crc = 0xffffffffu;
        foreach (var value in data)
        {
            crc ^= value;
            for (var bit = 0; bit < 8; bit += 1) crc = (crc & 1) == 1 ? 0xedb88320u ^ (crc >> 1) : crc >> 1;
        }
        return crc ^ 0xffffffffu;
    }

    private static Dictionary<char, string[]> BuildFont()
    {
        var rows = new Dictionary<char, string>
        {
            [' '] = "00000/00000/00000/00000/00000/00000/00000", ['?'] = "01110/10001/00001/00010/00100/00000/00100",
            ['.'] = "00000/00000/00000/00000/00000/00110/00110", [':'] = "00000/00110/00110/00000/00110/00110/00000",
            ['-'] = "00000/00000/00000/11111/00000/00000/00000", ['/'] = "00001/00010/00100/01000/10000/00000/00000",
            ['0'] = "01110/10001/10011/10101/11001/10001/01110", ['1'] = "00100/01100/00100/00100/00100/00100/01110",
            ['2'] = "01110/10001/00001/00010/00100/01000/11111", ['3'] = "11110/00001/00001/01110/00001/00001/11110",
            ['4'] = "00010/00110/01010/10010/11111/00010/00010", ['5'] = "11111/10000/10000/11110/00001/00001/11110",
            ['6'] = "01110/10000/10000/11110/10001/10001/01110", ['7'] = "11111/00001/00010/00100/01000/01000/01000",
            ['8'] = "01110/10001/10001/01110/10001/10001/01110", ['9'] = "01110/10001/10001/01111/00001/00001/01110",
            ['A'] = "01110/10001/10001/11111/10001/10001/10001", ['B'] = "11110/10001/10001/11110/10001/10001/11110",
            ['C'] = "01110/10001/10000/10000/10000/10001/01110", ['D'] = "11110/10001/10001/10001/10001/10001/11110",
            ['E'] = "11111/10000/10000/11110/10000/10000/11111", ['F'] = "11111/10000/10000/11110/10000/10000/10000",
            ['G'] = "01110/10001/10000/10111/10001/10001/01110", ['H'] = "10001/10001/10001/11111/10001/10001/10001",
            ['I'] = "01110/00100/00100/00100/00100/00100/01110", ['J'] = "00111/00010/00010/00010/10010/10010/01100",
            ['K'] = "10001/10010/10100/11000/10100/10010/10001", ['L'] = "10000/10000/10000/10000/10000/10000/11111",
            ['M'] = "10001/11011/10101/10101/10001/10001/10001", ['N'] = "10001/11001/10101/10011/10001/10001/10001",
            ['O'] = "01110/10001/10001/10001/10001/10001/01110", ['P'] = "11110/10001/10001/11110/10000/10000/10000",
            ['Q'] = "01110/10001/10001/10001/10101/10010/01101", ['R'] = "11110/10001/10001/11110/10100/10010/10001",
            ['S'] = "01111/10000/10000/01110/00001/00001/11110", ['T'] = "11111/00100/00100/00100/00100/00100/00100",
            ['U'] = "10001/10001/10001/10001/10001/10001/01110", ['V'] = "10001/10001/10001/10001/10001/01010/00100",
            ['W'] = "10001/10001/10001/10101/10101/10101/01010", ['X'] = "10001/10001/01010/00100/01010/10001/10001",
            ['Y'] = "10001/10001/01010/00100/00100/00100/00100", ['Z'] = "11111/00001/00010/00100/01000/10000/11111"
        };
        return rows.ToDictionary(item => item.Key, item => item.Value.Split('/'));
    }

    private readonly record struct Rgb(byte R, byte G, byte B);
}
