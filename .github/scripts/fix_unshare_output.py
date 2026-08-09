from pathlib import Path

p = Path("server/VolleyDraft.Api/Services/ZaloBotService.Unshare.cs")
text = p.read_text(encoding="utf-8")
old = "using Microsoft.EntityFrameworkCore;\nusing VolleyDraft.Api.Models;"
new = "using Microsoft.EntityFrameworkCore;\nusing VolleyDraft.Api.Contracts;\nusing VolleyDraft.Api.Models;"
if new not in text:
    if old not in text:
        raise SystemExit("unshare using anchor not found")
    p.write_text(text.replace(old, new, 1), encoding="utf-8")
print("unshare output namespace fixed")
