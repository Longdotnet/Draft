from pathlib import Path

path = Path("server/VolleyDraft.Api/Services/ZaloBotService.Unshare.cs")
text = path.read_text(encoding="utf-8")
old = "using Microsoft.EntityFrameworkCore;\nusing VolleyDraft.Api.Models;"
new = "using Microsoft.EntityFrameworkCore;\nusing VolleyDraft.Api.Contracts;\nusing VolleyDraft.Api.Models;"
if new not in text:
    if old not in text:
        raise SystemExit("unshare import anchor not found")
    text = text.replace(old, new, 1)
    path.write_text(text, encoding="utf-8")
print("unshare contract import ready")
