from pathlib import Path

path = Path('server/VolleyDraft.Api/Services/ZaloTeamCardService.cs')
text = path.read_text(encoding='utf-8')
old = '''        return new GeneratedTeamCard(\n            SimpleTeamCardPng.Render(session.Name, session.StartTime, session.Location, teams),\n            "image/png");'''
new = '''        byte[] poster;\n        try\n        {\n            poster = TournamentTeamPosterPng.Render(\n                session.Name,\n                session.StartTime,\n                session.Location,\n                teams);\n        }\n        catch (Exception exception)\n        {\n            // Keep @bot 10 operational even if a future premium-renderer change hits\n            // an unexpected Skia/font edge case in production. The old renderer is\n            // intentionally retained as a last-resort safety net.\n            logger.LogWarning(\n                exception,\n                "Tournament team poster render failed for Session={SessionId}; falling back to legacy card",\n                session.Id);\n            poster = SimpleTeamCardPng.Render(session.Name, session.StartTime, session.Location, teams);\n        }\n\n        return new GeneratedTeamCard(poster, "image/png");'''
if new in text:
    print('ZaloTeamCardService already patched')
elif old not in text:
    raise SystemExit('team-card render anchor not found')
else:
    path.write_text(text.replace(old, new, 1), encoding='utf-8')
    print('patched ZaloTeamCardService')
