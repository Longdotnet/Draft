from pathlib import Path


def replace_once(path: Path, old: str, new: str) -> None:
    text = path.read_text(encoding='utf-8')
    if new in text:
        return
    if old not in text:
        raise SystemExit(f'anchor not found in {path}: {old[:100]!r}')
    path.write_text(text.replace(old, new, 1), encoding='utf-8')


service = Path('server/VolleyDraft.Api/Services/ZaloTeamCardService.cs')
replace_once(
    service,
    '''        return new GeneratedTeamCard(\n            SimpleTeamCardPng.Render(session.Name, session.StartTime, session.Location, teams),\n            "image/png");''',
    '''        byte[] poster;\n        try\n        {\n            poster = TournamentTeamPosterPng.Render(\n                session.Name,\n                session.StartTime,\n                session.Location,\n                teams);\n        }\n        catch (Exception exception)\n        {\n            // Keep @bot 10 operational even if a future premium-renderer change hits\n            // an unexpected Skia/font edge case in production. The old renderer is\n            // intentionally retained as a last-resort safety net.\n            logger.LogWarning(\n                exception,\n                "Tournament team poster render failed for Session={SessionId}; falling back to legacy card",\n                session.Id);\n            poster = SimpleTeamCardPng.Render(session.Name, session.StartTime, session.Location, teams);\n        }\n\n        return new GeneratedTeamCard(poster, "image/png");''')

poster = Path('server/VolleyDraft.Api/Services/TournamentTeamPosterPng.cs')
replace_once(
    poster,
    'Color = WithAlpha(color, teamCount == 0 ? 22 : 36),',
    'Color = WithAlpha(color, (byte)(teamCount == 0 ? 22 : 36)),')
replace_once(
    poster,
    'canvas.DrawLine(56, 274, Width - 56, 274, separator);',
    'canvas.DrawLine(56, 300, Width - 56, 300, separator);')
replace_once(
    poster,
    '''        DrawMetric(canvas, rect.Right - 330, rect.Top + 26, "TEAM POWER", score, accent);\n        var playerCount = team.Slots.Sum(slot => Math.Max(1, slot.Players.Count));\n        DrawMetric(canvas, rect.Right - 178, rect.Top + 26, "PLAYERS", playerCount.ToString(CultureInfo.InvariantCulture), accent);\n        DrawMetric(canvas, rect.Right - 92, rect.Top + 26, "SLOTS", team.Slots.Count.ToString(CultureInfo.InvariantCulture), accent, 74);''',
    '''        DrawMetric(canvas, rect.Right - 330, rect.Top + 26, "TEAM POWER", score, accent, 128);\n        var playerCount = team.Slots.Sum(slot => Math.Max(1, slot.Players.Count));\n        DrawMetric(canvas, rect.Right - 192, rect.Top + 26, "PLAYERS", playerCount.ToString(CultureInfo.InvariantCulture), accent, 88);\n        DrawMetric(canvas, rect.Right - 94, rect.Top + 26, "SLOTS", team.Slots.Count.ToString(CultureInfo.InvariantCulture), accent, 78);''')
replace_once(
    poster,
    '''            var centerX = rect.Left + 88;\n            var centerY = rect.Top + 128;\n            DrawAvatar(canvas, centerX, centerY, 68, captain, accent, true);\n\n            DrawText(canvas, captain.Name, rect.Left + 26, rect.Top + 235, 27, Ink, true, rect.Width - 52, BlackTypeface);\n            DrawText(canvas, "ĐỘI TRƯỞNG", rect.Left + 26, rect.Top + 266, 13, accent, true, 150);''',
    '''            var centerX = rect.Left + 88;\n            var centerY = rect.Top + 105;\n            DrawAvatar(canvas, centerX, centerY, 56, captain, accent, true);\n\n            DrawText(canvas, captain.Name, rect.Left + 26, rect.Top + 191, 25, Ink, true, rect.Width - 52, BlackTypeface);\n            DrawText(canvas, "ĐỘI TRƯỞNG", rect.Left + 26, rect.Top + 216, 12, accent, true, 150);''')
replace_once(
    poster,
    '''            var centerX = rect.Left + 88;\n            var centerY = rect.Top + 128;''',
    '''            var centerX = rect.Left + 88;\n            var centerY = rect.Top + 105;''')
replace_once(
    poster,
    'canvas.DrawCircle(centerX, centerY, 68, ring);',
    'canvas.DrawCircle(centerX, centerY, 56, ring);')
replace_once(
    poster,
    'DrawText(canvas, "?", centerX - 19, centerY + 21, 54, WithAlpha(accent, 180), true, 50, BlackTypeface);',
    'DrawText(canvas, "?", centerX - 17, centerY + 18, 46, WithAlpha(accent, 180), true, 48, BlackTypeface);')
replace_once(
    poster,
    'DrawText(canvas, "CHƯA CHỌN CAPTAIN", rect.Left + 26, rect.Top + 235, 20, Soft, true, rect.Width - 52);',
    'DrawText(canvas, "CHƯA CHỌN CAPTAIN", rect.Left + 26, rect.Top + 191, 18, Soft, true, rect.Width - 52);')
replace_once(
    poster,
    '''        DrawText(canvas, "TEAM POWER", rect.Left + 26, rect.Bottom - 48, 12, Muted, true, 100);\n        DrawText(canvas, average, rect.Right - 88, rect.Bottom - 40, 28, accent, true, 68, BlackTypeface);''',
    '''        DrawText(canvas, "TEAM POWER", rect.Left + 26, rect.Bottom - 20, 11, Muted, true, 100);\n        DrawText(canvas, average, rect.Right - 78, rect.Bottom - 17, 24, accent, true, 58, BlackTypeface);''')
replace_once(
    poster,
    'var random = new Random(StableSeed(sessionName));',
    'var random = new Random(StableSeed(sessionName) & int.MaxValue);')

print('tournament poster service and layout patch applied')
