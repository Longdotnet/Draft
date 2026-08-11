from pathlib import Path

path = Path("server/VolleyDraft.Api/Services/ZaloBotIntelligence.cs")
text = path.read_text(encoding="utf-8")
old = '''    public static bool IsUnshareSlotRequest(string value)
    {
        var q = Normalize(value).Replace("@", string.Empty, StringComparison.Ordinal);
        var mentionsShare = Has(q,
            "share slot", "share", "chung slot", "danh chung slot", "choi chung slot",
            "slot thay phien", "thay phien");
        if (!mentionsShare) return false;
        var stopsSharing = Has(q,
            "khong share", "ko share", "khong chung slot", "ko chung slot",
            "khong danh chung slot", "khong choi chung slot", "khong thay phien",
            "huy share", "bo share", "tach share", "tach slot");
        return stopsSharing;
    }
'''
new = '''    public static bool IsUnshareSlotRequest(string value)
    {
        var q = Normalize(value).Replace("@", string.Empty, StringComparison.Ordinal);
        var mentionsShare = Has(q,
            "share slot", "share", "chung slot", "danh chung slot", "choi chung slot",
            "slot thay phien", "thay phien");
        if (!mentionsShare) return false;

        if (Has(q,
                "khong share", "ko share", "khong chung slot", "ko chung slot",
                "khong danh chung slot", "khong choi chung slot", "khong thay phien"))
            return true;

        // Keep destructive verbs contextual. A participant named Huy followed by
        // "share slot" must not be read as the unaccented verb "huy share".
        if (Regex.IsMatch(
                value,
                @"(?:hủy|huỷ|bỏ|tách)\\s+(?:share|share\\s+slot|slot)",
                RegexOptions.IgnoreCase | RegexOptions.CultureInvariant))
            return true;

        return Regex.IsMatch(
            q,
            @"^(?:(?:bot|npc)\\s+)?(?:(?:tui|toi|minh|em|anh|chi|tao)\\s+)?(?:huy|bo|tach)\\s+(?:share|share\\s+slot|slot)\\b|\\bmuon\\s+(?:huy|bo|tach)\\s+(?:share|share\\s+slot|slot)\\b",
            RegexOptions.CultureInvariant);
    }
'''
count = text.count(old)
if count != 1:
    raise SystemExit(f"expected one post-patch IsUnshareSlotRequest method, found {count}")
path.write_text(text.replace(old, new, 1), encoding="utf-8")
