from pathlib import Path


def replace_once(path: str, old: str, new: str) -> None:
    p = Path(path)
    text = p.read_text(encoding="utf-8")
    if new in text:
        print(f"already patched: {path}")
        return
    if old not in text:
        raise SystemExit(f"anchor not found in {path}: {old[:120]!r}")
    p.write_text(text.replace(old, new, 1), encoding="utf-8")


catalog = "server/VolleyDraft.Api/Services/ZaloOverbookMessageCatalog.cs"
replace_once(
    catalog,
    '    internal const int StubbornStorageKey = 1004;\n',
    '    internal const int StubbornStorageKey = 1004;\n    internal const int AdvancedExactStorageOffset = 2000;\n',
)
replace_once(
    catalog,
    '''    internal static Dictionary<int, IReadOnlyList<string>> GetUiBanks(IReadOnlyDictionary<int, List<string>> overrides) =>\n        overrides\n            .Where(pair => pair.Key is >= 1 and <= 100 && pair.Value.Count > 0)\n            .ToDictionary(pair => pair.Key, pair => (IReadOnlyList<string>)pair.Value);\n\n    internal static bool TryGetCustomStageBank(''',
    '''    internal static Dictionary<int, IReadOnlyList<string>> GetUiBanks(IReadOnlyDictionary<int, List<string>> overrides) =>\n        overrides\n            .Where(pair => pair.Key > AdvancedExactStorageOffset &&\n                           pair.Key <= AdvancedExactStorageOffset + 100 &&\n                           pair.Value.Count > 0)\n            .ToDictionary(\n                pair => pair.Key - AdvancedExactStorageOffset,\n                pair => (IReadOnlyList<string>)pair.Value);\n\n    internal static int GetAdvancedExactStorageKey(int reminderNumber)\n    {\n        if (reminderNumber is < 1 or > 100)\n            throw new ArgumentOutOfRangeException(nameof(reminderNumber));\n        return AdvancedExactStorageOffset + reminderNumber;\n    }\n\n    internal static bool TryGetAdvancedExactBank(\n        IReadOnlyDictionary<int, List<string>> overrides,\n        int reminderNumber,\n        out IReadOnlyList<string> bank)\n    {\n        var storageKey = GetAdvancedExactStorageKey(reminderNumber);\n        if (overrides.TryGetValue(storageKey, out var custom) && custom.Count > 0)\n        {\n            bank = custom;\n            return true;\n        }\n        bank = [];\n        return false;\n    }\n\n    internal static bool TryGetCustomStageBank(''',
)

service = "server/VolleyDraft.Api/Services/ZaloOverbookService.cs"
replace_once(
    service,
    '''        var result = existing\n            .Where(pair => pair.Key > 100)\n            .ToDictionary(pair => pair.Key, pair => pair.Value.ToList());\n        foreach (var pair in banks.Where(pair => pair.Key is >= 1 and <= 100))\n        {\n            var normalized = NormalizeMessages(pair.Value, 20);\n            if (normalized.Count > 0) result[pair.Key] = normalized;\n        }''',
    '''        var result = existing\n            .Where(pair => pair.Key <= ZaloOverbookMessageCatalog.AdvancedExactStorageOffset ||\n                           pair.Key > ZaloOverbookMessageCatalog.AdvancedExactStorageOffset + 100)\n            .ToDictionary(pair => pair.Key, pair => pair.Value.ToList());\n        foreach (var pair in banks.Where(pair => pair.Key is >= 1 and <= 100))\n        {\n            var normalized = NormalizeMessages(pair.Value, 20);\n            if (normalized.Count > 0)\n                result[ZaloOverbookMessageCatalog.GetAdvancedExactStorageKey(pair.Key)] = normalized;\n        }''',
)

reminder = "server/VolleyDraft.Api/Services/ZaloOverbookReminder.cs"
replace_once(
    reminder,
    '''        if (useAdminPool && state.ReminderMessageBanks.TryGetValue(reminderNumber, out var exactBank) && exactBank.Count > 0)\n        {\n            // Backwards-compatible advanced override: an exact reminder number still wins.\n            pool = exactBank;\n            tierPrefix = $"reminder-{reminderNumber}:";\n        }\n        else if (useAdminPool && ZaloOverbookMessageCatalog.TryGetCustomStageBank(state.ReminderMessageBanks, stage, out var stageBank))\n        {\n            pool = stageBank;\n            tierPrefix = $"stage-custom-{stage}:";\n        }\n        else if (useAdminPool)\n        {\n            pool = ZaloOverbookMessageCatalog.GetDefaultStageBank(stage);\n            tierPrefix = $"stage-default-{stage}:";\n        }''',
    '''        if (useAdminPool && ZaloOverbookMessageCatalog.TryGetAdvancedExactBank(state.ReminderMessageBanks, reminderNumber, out var exactBank))\n        {\n            // New advanced override uses a separate storage range so legacy #1-#100\n            // data from the previous UI cannot silently override the staged system.\n            pool = exactBank;\n            tierPrefix = $"advanced-reminder-{reminderNumber}:";\n        }\n        else if (useAdminPool && ZaloOverbookMessageCatalog.TryGetCustomStageBank(state.ReminderMessageBanks, stage, out var stageBank))\n        {\n            pool = stageBank;\n            tierPrefix = $"stage-custom-{stage}:";\n        }\n        else if (useAdminPool && state.ReminderMessageBanks.TryGetValue(reminderNumber, out var legacyBank) && legacyBank.Count > 0)\n        {\n            // Before an old session is saved in the new UI, keep its previous\n            // per-reminder content working. Once all four stage banks are saved,\n            // stage banks take priority and these legacy rows become inert.\n            pool = legacyBank;\n            tierPrefix = $"legacy-reminder-{reminderNumber}:";\n        }\n        else if (useAdminPool)\n        {\n            pool = ZaloOverbookMessageCatalog.GetDefaultStageBank(stage);\n            tierPrefix = $"stage-default-{stage}:";\n        }''',
)

tests = "server/VolleyDraft.Api.Tests/ZaloOverbookMessageCatalogTests.cs"
replace_once(
    tests,
    '''        var overrides = new Dictionary<int, List<string>>\n        {\n            [10] = ["special #10 {names}"],\n            [ZaloOverbookMessageCatalog.LightStorageKey] = ["stage {names}"],\n        };\n\n        var exact = ZaloOverbookMessageCatalog.GetUiBanks(overrides);\n\n        Assert.Single(exact);\n        Assert.Equal(["special #10 {names}"], exact[10]);\n        Assert.DoesNotContain(ZaloOverbookMessageCatalog.LightStorageKey, exact.Keys);''',
    '''        var overrides = new Dictionary<int, List<string>>\n        {\n            [10] = ["legacy #10 {names}"],\n            [ZaloOverbookMessageCatalog.GetAdvancedExactStorageKey(10)] = ["special #10 {names}"],\n            [ZaloOverbookMessageCatalog.LightStorageKey] = ["stage {names}"],\n        };\n\n        var exact = ZaloOverbookMessageCatalog.GetUiBanks(overrides);\n\n        Assert.Single(exact);\n        Assert.Equal(["special #10 {names}"], exact[10]);\n        Assert.DoesNotContain(ZaloOverbookMessageCatalog.LightStorageKey, exact.Keys);''',
)

print("stage storage migration patch applied")
