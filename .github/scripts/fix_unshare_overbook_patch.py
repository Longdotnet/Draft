from pathlib import Path

# Fix namespace needed by the generated partial bot file.
p = Path("server/VolleyDraft.Api/Services/ZaloBotService.Unshare.cs")
text = p.read_text(encoding="utf-8")
if "using VolleyDraft.Api.Contracts;" not in text:
    text = text.replace(
        "using Microsoft.EntityFrameworkCore;\nusing VolleyDraft.Api.Models;",
        "using Microsoft.EntityFrameworkCore;\nusing VolleyDraft.Api.Contracts;\nusing VolleyDraft.Api.Models;",
        1,
    )
p.write_text(text, encoding="utf-8")

# A positive phrase like "muốn share nữa" must stay ShareSlot. The negative
# phrases already match "không share" / "không chung slot" / "không thay phiên".
p = Path("server/VolleyDraft.Api/Services/ZaloBotIntelligence.cs")
text = p.read_text(encoding="utf-8")
text = text.replace(
    '            "huy share", "bo share", "tach share", "tach slot",\n            "share nua", "chung slot nua", "thay phien nua");',
    '            "huy share", "bo share", "tach share", "tach slot");',
)
p.write_text(text, encoding="utf-8")

p = Path("server/VolleyDraft.Api.Tests/ZaloUnshareIntentTests.cs")
text = p.read_text(encoding="utf-8")
anchor = '''    [Fact]\n    public void Normal_share_is_still_share_slot()\n    {\n        Assert.Equal(\n            ZaloBotIntent.ShareSlot,\n            ZaloBotIntelligence.ClassifyDeterministically("tui muốn share slot với To An").Intent);\n    }\n'''
replacement = '''    [Theory]\n    [InlineData("tui muốn share slot với To An")]\n    [InlineData("tui muốn share nữa với To An")]\n    public void Positive_share_phrases_are_still_share_slot(string text)\n    {\n        Assert.Equal(\n            ZaloBotIntent.ShareSlot,\n            ZaloBotIntelligence.ClassifyDeterministically(text).Intent);\n    }\n'''
if replacement not in text:
    if anchor not in text:
        raise SystemExit("unshare test anchor not found")
    text = text.replace(anchor, replacement, 1)
p.write_text(text, encoding="utf-8")

# Harden the manual immediate-reminder path against double-click/retry on the
# exact same poll snapshot + option scope + targets. The same incident must not
# reset ReminderCount and emit reminder #1 twice.
p = Path("server/VolleyDraft.Api/Services/ZaloOverbookManualReminder.cs")
text = p.read_text(encoding="utf-8")
old = '''        var now = DateTimeOffset.UtcNow;\n        ApplyConfirmedTargets(state, normalized, now);\n        state.IncidentKey = BuildAdminIncidentKey(\n            observation.Poll.Id,\n            observation.SelectedOptionIds,\n            observation.Poll.UpdatedAtUnixMs,\n            observation.Capacity.EffectiveSlotCount,\n            normalized);\n        await store.SaveAsync(state, cancellationToken);\n\n        if (!remindNow)\n            return ServiceResult<ZaloOverbookStatusResponse>.Success(\n                await BuildStatusAsync(observation, state, cancellationToken));\n'''
new = '''        var now = DateTimeOffset.UtcNow;\n        var incidentKey = BuildAdminIncidentKey(\n            observation.Poll.Id,\n            observation.SelectedOptionIds,\n            observation.Poll.UpdatedAtUnixMs,\n            observation.Capacity.EffectiveSlotCount,\n            normalized);\n        var sameConfirmedIncident =\n            string.Equals(state.IncidentKey, incidentKey, StringComparison.Ordinal) &&\n            state.OrderConfidence == "AdminConfirmed" &&\n            state.CurrentTargetVoterIds.SequenceEqual(normalized, StringComparer.Ordinal);\n\n        if (!sameConfirmedIncident)\n        {\n            ApplyConfirmedTargets(state, normalized, now);\n            state.IncidentKey = incidentKey;\n            await store.SaveAsync(state, cancellationToken);\n        }\n\n        if (!remindNow || sameConfirmedIncident && state.ReminderCount > 0)\n            return ServiceResult<ZaloOverbookStatusResponse>.Success(\n                await BuildStatusAsync(observation, state, cancellationToken));\n'''
if old not in text:
    if new not in text:
        raise SystemExit("manual reminder idempotency anchor not found")
else:
    text = text.replace(old, new, 1)

# Explicitly require a live Zalo destination for immediate sends.
old2 = '''        if (remindNow && !owned.BotEnabled)\n            return ServiceResult<ZaloOverbookStatusResponse>.Failure(StatusCodes.Status400BadRequest, "Bot Zalo của trận đang tắt. Bật bot trước khi nhắc ngay.");\n'''
new2 = '''        if (remindNow && !owned.BotEnabled)\n            return ServiceResult<ZaloOverbookStatusResponse>.Failure(StatusCodes.Status400BadRequest, "Bot Zalo của trận đang tắt. Bật bot trước khi nhắc ngay.");\n        if (remindNow && (owned.ZaloConnection is null || string.IsNullOrWhiteSpace(owned.ZaloGroupId)))\n            return ServiceResult<ZaloOverbookStatusResponse>.Failure(StatusCodes.Status400BadRequest, "Trận chưa liên kết đầy đủ tài khoản/group Zalo để gửi mention.");\n'''
if old2 in text:
    text = text.replace(old2, new2, 1)
p.write_text(text, encoding="utf-8")

print("follow-up fixes applied")
