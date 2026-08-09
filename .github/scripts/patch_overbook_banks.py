from pathlib import Path


def rep(path, old, new):
    p=Path(path); s=p.read_text(encoding='utf-8')
    if old not in s: raise SystemExit(f'anchor missing {path}: {old[:100]}')
    p.write_text(s.replace(old,new,1),encoding='utf-8')

# Contracts
p=Path('server/VolleyDraft.Api/Contracts/ZaloOverbookContracts.cs'); s=p.read_text(encoding='utf-8')
s=s.replace('    IReadOnlyList<string>? StrictMessages);','''    IReadOnlyList<string>? StrictMessages,\n    IReadOnlyDictionary<int, IReadOnlyList<string>>? ReminderMessageBanks = null);''')
s=s.replace('    IReadOnlyList<string> StrictMessages,\n    string OrderConfidence,','''    IReadOnlyList<string> StrictMessages,\n    IReadOnlyDictionary<int, IReadOnlyList<string>> ReminderMessageBanks,\n    string OrderConfidence,''')
s += '''\npublic sealed record CopyZaloOverbookSettingsRequest(\n    string SourceSessionId,\n    IReadOnlyList<string> TargetSessionIds,\n    bool CopyMessages = true,\n    bool CopyTiming = false,\n    bool CopyMaxReminders = false,\n    bool CopyMessageSource = false);\n'''
p.write_text(s,encoding='utf-8')

# State: dictionary stored in same row as JSON, with safe schema upgrade.
p='server/VolleyDraft.Api/Services/ZaloOverbookStateStore.cs'
s=Path(p).read_text(encoding='utf-8')
s=s.replace('    public List<string> StrictMessages { get; set; } = [];','    public List<string> StrictMessages { get; set; } = [];\n    public Dictionary<int, List<string>> ReminderMessageBanks { get; set; } = [];')
s=s.replace('        await db.Database.ExecuteSqlRawAsync(sql, cancellationToken);','''        await db.Database.ExecuteSqlRawAsync(sql, cancellationToken);\n        await EnsureReminderBanksColumnAsync(cancellationToken);''',1)
s=s.replace('                "StrictMessagesJson" TEXT NOT NULL DEFAULT \'[]\',','                "StrictMessagesJson" TEXT NOT NULL DEFAULT \'[]\',\n                "ReminderMessageBanksJson" TEXT NOT NULL DEFAULT \'{}\',')
s=s.replace('"MessageSource", "FriendlyMessagesJson", "SeriousMessagesJson", "StrictMessagesJson",','"MessageSource", "FriendlyMessagesJson", "SeriousMessagesJson", "StrictMessagesJson", "ReminderMessageBanksJson",')
s=s.replace('@MessageSource, @FriendlyMessagesJson, @SeriousMessagesJson, @StrictMessagesJson,','@MessageSource, @FriendlyMessagesJson, @SeriousMessagesJson, @StrictMessagesJson, @ReminderMessageBanksJson,')
s=s.replace('                "StrictMessagesJson" = excluded."StrictMessagesJson",','                "StrictMessagesJson" = excluded."StrictMessagesJson",\n                "ReminderMessageBanksJson" = excluded."ReminderMessageBanksJson",')
s=s.replace('        AddParameter(command, "@StrictMessagesJson", Serialize(state.StrictMessages));','        AddParameter(command, "@StrictMessagesJson", Serialize(state.StrictMessages));\n        AddParameter(command, "@ReminderMessageBanksJson", JsonSerializer.Serialize(state.ReminderMessageBanks, JsonOptions));')
s=s.replace('        StrictMessages = Deserialize(ReadString(reader, "StrictMessagesJson")),','        StrictMessages = Deserialize(ReadString(reader, "StrictMessagesJson")),\n        ReminderMessageBanks = DeserializeBanks(ReadString(reader, "ReminderMessageBanksJson")),')
insert='''\n    private async Task EnsureReminderBanksColumnAsync(CancellationToken cancellationToken)\n    {\n        await using var command = await CreateCommandAsync("SELECT * FROM \\\"ZaloOverbookStates\\\" LIMIT 0;", cancellationToken);\n        await using var reader = await command.ExecuteReaderAsync(cancellationToken);\n        var hasColumn = Enumerable.Range(0, reader.FieldCount).Any(i => string.Equals(reader.GetName(i), "ReminderMessageBanksJson", StringComparison.OrdinalIgnoreCase));\n        await reader.DisposeAsync();\n        if (!hasColumn)\n            await db.Database.ExecuteSqlRawAsync("ALTER TABLE \\\"ZaloOverbookStates\\\" ADD COLUMN \\\"ReminderMessageBanksJson\\\" TEXT NOT NULL DEFAULT '{}';", cancellationToken);\n    }\n\n    private static Dictionary<int, List<string>> DeserializeBanks(string? json)\n    {\n        try { return JsonSerializer.Deserialize<Dictionary<int, List<string>>>(json ?? "{}", JsonOptions) ?? []; }\n        catch (JsonException) { return []; }\n    }\n'''
s=s.replace('    private static string Serialize(IReadOnlyList<string> values) => JsonSerializer.Serialize(values, JsonOptions);',insert+'\n    private static string Serialize(IReadOnlyList<string> values) => JsonSerializer.Serialize(values, JsonOptions);')
Path(p).write_text(s,encoding='utf-8')

# Catalog: 20 gen-z templates per bank, stage text changes per reminder/range.
Path('server/VolleyDraft.Api/Services/ZaloOverbookMessageCatalog.cs').write_text(r'''namespace VolleyDraft.Api.Services;

internal static class ZaloOverbookMessageCatalog
{
    private static readonly string[] Frames =
    [
        "Ê {names}, đủ {capacity} slot rồi nha 😭 {stage} Gỡ vote giúp anh em cái.",
        "{names} ơi, tàu đủ ghế rồi mà bro còn leo lên 😭 {stage} Nhường kèo cho anh em nha.",
        "Alo {names}, hiện tại {effectiveSlotCount}/{capacity} rồi =)) {stage} Bro đang đứng ngoài cửa đó.",
        "{names} check lại vote nha, full slot mất tiêu rồi 🥲 {stage} Đừng làm BTC khó xử.",
        "Ủa {names} 😭 thấy {capacity}/{capacity} mà vẫn bấm vô được hay vậy. {stage} Gỡ vote hộ cái.",
        "{names} ơi thương anh em thì nhìn số slot trước khi click nha =)) đang dư {excessCount} người. {stage}",
        "{names} bro ơi bot nhắc tới lần {reminderNumber} rồi đó 😭 {stage} Full slot rồi, gỡ vote dùm.",
        "{names} đọc số được không trời 😭 {effectiveSlotCount}/{capacity} mà vẫn cố chen. {stage}",
        "Alo {names}, vote dư không làm sân mọc thêm đâu nha =)) {stage} Gỡ giùm cái.",
        "{names} ơi đây là poll đăng ký chứ không phải game xếp hình 😭 hết chỗ là hết chỗ. {stage}",
        "Bot đã réo rồi nha {names} =)) {stage} Gỡ vote trước khi cả group réo tên.",
        "{names}, slot thứ {firstExcessSlot} không phải slot VIP đâu 😭 {stage} Gỡ vote đi bro.",
        "Ủa {names}, bro định dùng sức mạnh niềm tin biến {capacity} slot thành {effectiveSlotCount} slot hả =)) {stage}",
        "{names} ơi lần {reminderNumber} rồi 😭 cái nút bỏ vote nó không thu phí đâu bro. {stage}",
        "{names} full slot từ đời nào rồi mà bro vẫn lì như bug production vậy =)) {stage} Gỡ vote.",
        "Thông báo khẩn: {names} vẫn đang cố chứng minh {effectiveSlotCount} ≤ {capacity} 😭 toán học đang khóc. {stage}",
        "{names} mắt thấy {capacity}/{capacity}, tay vẫn vote. Một pha xử lý đi vào lòng đất =)) {stage}",
        "{names} ơi bot nhắc tới mức này rồi mà chưa gỡ thì đúng là đam mê chen slot 😭 {stage}",
        "Bro {names} định đứng slot dư tới lúc draft luôn hả 😭 draft bằng niềm tin à? {stage} Gỡ vote giùm.",
        "{names}, cả hệ thống đang chạy ổn cho tới khi bro phát minh slot thứ {effectiveSlotCount} =)) {stage} Gỡ lẹ."
    ];

    internal static IReadOnlyList<string> GetBank(int reminderNumber)
    {
        var stage = reminderNumber switch
        {
            1 => "Lần đầu bot nhắc nhẹ nhàng thôi nha.",
            2 => "Lần 2 rồi nha bro, cứu bot một pha.",
            3 => "Lần 3 bắt đầu nghiêm túc rồi đó.",
            4 => "Lần 4 rồi, đừng giả bộ chưa thấy nha =))",
            5 => "Lần 5, độ lì đang tăng hơi nhanh đó bro.",
            6 => "Lần 6 rồi, bot bắt đầu bất lực thiệt nha 😭",
            7 => "Lần 7 rồi, bro đang unlock danh hiệu tai trâu đó =))",
            <= 10 => $"Lần {reminderNumber}, độ lì slot đang lên rank cao rồi đó.",
            <= 20 => $"Lần {reminderNumber}, hội đồng tai trâu đang gọi tên bro rồi 😭",
            <= 40 => $"Lần {reminderNumber}, bot bắt đầu nghi ngờ nút bỏ vote bị tàng hình =))",
            <= 70 => $"Lần {reminderNumber}, đây không còn là quên nữa, đây là một hành trình.",
            _ => $"Lần {reminderNumber}, huyền thoại lì slot vẫn chưa chịu kết thúc 😭"
        };
        return Frames.Select(frame => frame.Replace("{stage}", stage, StringComparison.Ordinal)).ToList();
    }

    internal static Dictionary<int, IReadOnlyList<string>> GetUiBanks(IReadOnlyDictionary<int, List<string>> overrides)
    {
        var result = new Dictionary<int, IReadOnlyList<string>>();
        for (var i = 1; i <= 7; i++) result[i] = overrides.TryGetValue(i, out var custom) && custom.Count > 0 ? custom : GetBank(i);
        foreach (var pair in overrides.Where(pair => pair.Key is >= 8 and <= 100 && pair.Value.Count > 0)) result[pair.Key] = pair.Value;
        return result;
    }
}
''',encoding='utf-8')

# Service max100, banks, copy endpoint logic.
p='server/VolleyDraft.Api/Services/ZaloOverbookService.cs'; s=Path(p).read_text(encoding='utf-8')
s=s.replace('if (request.MaxReminders is < 1 or > 20)','if (request.MaxReminders is < 1 or > 100)').replace('Số lần nhắc tối đa phải từ 1 đến 20.','Số lần nhắc tối đa phải từ 1 đến 100.')
s=s.replace('        state.StrictMessages = NormalizeMessages(request.StrictMessages);','''        state.StrictMessages = NormalizeMessages(request.StrictMessages);\n        if (request.ReminderMessageBanks is not null)\n            state.ReminderMessageBanks = NormalizeBanks(request.ReminderMessageBanks);''')
# add methods before final class brace
idx=s.rfind('\n}')
methods=r'''
    public async Task<ServiceResult<int>> CopySettingsAsync(string adminUserId, string sessionId, CopyZaloOverbookSettingsRequest request, CancellationToken cancellationToken = default)
    {
        if (!string.Equals(sessionId, request.SourceSessionId, StringComparison.Ordinal))
            return ServiceResult<int>.Failure(StatusCodes.Status400BadRequest, "Source session không khớp route.");
        var sourceOwned = await GetOwnedSessionAsync(adminUserId, sessionId, cancellationToken);
        if (sourceOwned is null) return ServiceResult<int>.Failure(StatusCodes.Status404NotFound, "Không tìm thấy trận nguồn.");
        var source = await GetOrCreateStateAsync(sessionId, cancellationToken);
        var targets = request.TargetSessionIds.Where(id => !string.Equals(id, sessionId, StringComparison.Ordinal)).Distinct(StringComparer.Ordinal).Take(30).ToList();
        var copied = 0;
        foreach (var targetId in targets)
        {
            if (await GetOwnedSessionAsync(adminUserId, targetId, cancellationToken) is null) continue;
            var target = await GetOrCreateStateAsync(targetId, cancellationToken);
            if (request.CopyMessages)
            {
                target.FriendlyMessages = source.FriendlyMessages.ToList();
                target.SeriousMessages = source.SeriousMessages.ToList();
                target.StrictMessages = source.StrictMessages.ToList();
                target.ReminderMessageBanks = source.ReminderMessageBanks.ToDictionary(pair => pair.Key, pair => pair.Value.ToList());
            }
            if (request.CopyTiming) { target.GraceMinutes = source.GraceMinutes; target.ReminderIntervalMinutes = source.ReminderIntervalMinutes; }
            if (request.CopyMaxReminders) target.MaxReminders = source.MaxReminders;
            if (request.CopyMessageSource) target.MessageSource = source.MessageSource;
            // Runtime incident state is deliberately untouched.
            await store.SaveAsync(target, cancellationToken);
            copied++;
        }
        return ServiceResult<int>.Success(copied);
    }

    private static Dictionary<int, List<string>> NormalizeBanks(IReadOnlyDictionary<int, IReadOnlyList<string>> banks) =>
        banks.Where(pair => pair.Key is >= 1 and <= 100)
            .ToDictionary(pair => pair.Key, pair => NormalizeMessages(pair.Value).Take(20).ToList());
'''
s=s[:idx]+methods+s[idx:]
Path(p).write_text(s,encoding='utf-8')

# Reminder selection: custom exact bank -> catalog fallback, firstExcessSlot placeholder.
p='server/VolleyDraft.Api/Services/ZaloOverbookReminder.cs'; s=Path(p).read_text(encoding='utf-8')
old='''        var useAdminPool = state.MessageSource == ZaloOverbookMessageSource.AdminPool;\n        var pool = tone switch\n        {\n            "strict" when useAdminPool && state.StrictMessages.Count > 0 => state.StrictMessages,\n            "serious" when useAdminPool && state.SeriousMessages.Count > 0 => state.SeriousMessages,\n            "friendly" when useAdminPool && state.FriendlyMessages.Count > 0 => state.FriendlyMessages,\n            "strict" => DefaultStrictMessages,\n            "serious" => DefaultSeriousMessages,\n            _ => DefaultFriendlyMessages\n        };\n        var tierPrefix = tone + ":";'''
new='''        var useAdminPool = state.MessageSource == ZaloOverbookMessageSource.AdminPool;\n        IReadOnlyList<string> pool;\n        string tierPrefix;\n        if (useAdminPool && state.ReminderMessageBanks.TryGetValue(reminderNumber, out var exactBank) && exactBank.Count > 0)\n        {\n            pool = exactBank;\n            tierPrefix = $"reminder-{reminderNumber}:";\n        }\n        else if (useAdminPool)\n        {\n            pool = ZaloOverbookMessageCatalog.GetBank(reminderNumber);\n            tierPrefix = $"catalog-{reminderNumber}:";\n        }\n        else\n        {\n            pool = tone switch\n            {\n                "strict" => DefaultStrictMessages,\n                "serious" => DefaultSeriousMessages,\n                _ => DefaultFriendlyMessages\n            };\n            tierPrefix = tone + ":";\n        }'''
if old not in s: raise SystemExit('reminder pool anchor missing')
s=s.replace(old,new,1)
s=s.replace('.Replace("{capacity}", capacity.ToString(), StringComparison.OrdinalIgnoreCase)','.Replace("{capacity}", capacity.ToString(), StringComparison.OrdinalIgnoreCase)\n            .Replace("{firstExcessSlot}", (capacity + 1).ToString(), StringComparison.OrdinalIgnoreCase)')
Path(p).write_text(s,encoding='utf-8')

# Observation status builders need new positional field. Find all StrictMessages then OrderConfidence constructor spots.
for p in ['server/VolleyDraft.Api/Services/ZaloOverbookObservation.cs']:
    s=Path(p).read_text(encoding='utf-8')
    s=s.replace('''        state.StrictMessages.Count > 0 ? state.StrictMessages : DefaultStrictMessages,\n        state.OrderConfidence,''','''        state.StrictMessages.Count > 0 ? state.StrictMessages : DefaultStrictMessages,\n        ZaloOverbookMessageCatalog.GetUiBanks(state.ReminderMessageBanks),\n        state.OrderConfidence,''')
    Path(p).write_text(s,encoding='utf-8')

# Program copy route
p='server/VolleyDraft.Api/Program.cs'; s=Path(p).read_text(encoding='utf-8')
anchor='''sessions.MapPost("/{sessionId}/zalo-overbook/confirm", async ('''
route='''sessions.MapPost("/{sessionId}/zalo-overbook/copy", async (\n    HttpContext httpContext,\n    string sessionId,\n    CopyZaloOverbookSettingsRequest request,\n    ZaloOverbookService service,\n    CancellationToken cancellationToken) =>\n{\n    var userId = httpContext.User.GetUserId();\n    return userId is null\n        ? Results.Unauthorized()\n        : (await service.CopySettingsAsync(userId, sessionId, request, cancellationToken)).ToHttpResult();\n});\n'''
if anchor not in s: raise SystemExit('program anchor')
s=s.replace(anchor,route+anchor,1); Path(p).write_text(s,encoding='utf-8')

# Frontend targeted edits.
p='src/components/ZaloOverbookAdminPanel.tsx'; s=Path(p).read_text(encoding='utf-8')
s=s.replace('import { AlertTriangle, Bot, RefreshCw, Save, ShieldCheck }','import { AlertTriangle, Bot, Copy, RefreshCw, Save, ShieldCheck }')
s=s.replace('  strictMessages: string[];','  strictMessages: string[];\n  reminderMessageBanks: Record<string, string[]>;')
s=s.replace('  strictMessages: string;\n};','  strictMessages: string;\n  reminderMessageBanks: Record<number, string>;\n};')
s=s.replace('  strictMessages: "",\n};','  strictMessages: "",\n  reminderMessageBanks: {},\n};')
s=s.replace('  const [busy, setBusy] = useState(false);','''  const [busy, setBusy] = useState(false);\n  const [reminderEditor, setReminderEditor] = useState(1);\n  const [copyTargets, setCopyTargets] = useState<string[]>([]);\n  const [copyTiming, setCopyTiming] = useState(false);\n  const [copyMax, setCopyMax] = useState(false);\n  const [copySource, setCopySource] = useState(false);''')
s=s.replace('      strictMessages: next.strictMessages.join("\\n"),','''      strictMessages: next.strictMessages.join("\\n"),\n      reminderMessageBanks: Object.fromEntries(Object.entries(next.reminderMessageBanks ?? {}).map(([key, lines]) => [Number(key), lines.join("\\n")])),''')
s=s.replace('          strictMessages: splitLines(form.strictMessages),','''          strictMessages: splitLines(form.strictMessages),\n          reminderMessageBanks: Object.fromEntries(Object.entries(form.reminderMessageBanks).map(([key, value]) => [key, splitLines(value)])),''')
s=s.replace('max={20}', 'max={100}')
# If max attr absent, generic replace on input known snippet
s=s.replace('max="20"','max="100"')
# Replace source message grid with per reminder editor by injecting before Save button area.
needle='''          <button type="button" onClick={() => void saveSettings()}'''
insert=r'''          {form.messageSource === "AdminPool" ? (
            <div style={{ marginTop: 16, padding: 14, borderRadius: 12, background: "rgba(15, 23, 42, 0.72)" }}>
              <strong>Kho câu theo từng lần nhắc</strong>
              <p style={{ color: "#94a3b8", fontSize: 13 }}>Mỗi dòng là 1 câu, tối đa 20 câu/lần. #1–#7 có sẵn 20 câu Gen Z; #8–#100 dùng fallback tăng dần độ cà khịa nếu bạn chưa override.</p>
              <div style={{ display: "flex", gap: 8, flexWrap: "wrap", alignItems: "center" }}>
                <label>Lần # <input type="number" min={1} max={100} value={reminderEditor} onChange={(e) => setReminderEditor(Math.max(1, Math.min(100, Number(e.target.value) || 1)))} style={{ ...inputStyle, width: 90 }} /></label>
                <button type="button" style={{ ...buttonStyle, background: "#334155", color: "#fff" }} onClick={() => setForm(current => ({ ...current, reminderMessageBanks: { ...current.reminderMessageBanks, [reminderEditor]: current.reminderMessageBanks[Math.max(1, reminderEditor - 1)] ?? "" } }))}>Copy từ lần trước</button>
              </div>
              <textarea rows={8} value={form.reminderMessageBanks[reminderEditor] ?? ""} onChange={(e) => setForm(current => ({ ...current, reminderMessageBanks: { ...current.reminderMessageBanks, [reminderEditor]: e.target.value } }))} placeholder="Để trống để dùng kho mặc định/fallback của hệ thống" style={{ ...inputStyle, marginTop: 10, resize: "vertical" }} />
              <div style={{ color: "#94a3b8", fontSize: 12, marginTop: 6 }}>Placeholder: {'{names}'} {'{capacity}'} {'{firstExcessSlot}'} {'{effectiveSlotCount}'} {'{rawVoterCount}'} {'{excessCount}'} {'{reminderNumber}'} {'{sessionName}'}</div>
            </div>
          ) : null}

          <div style={{ marginTop: 16, padding: 14, borderRadius: 12, background: "rgba(15, 23, 42, 0.72)" }}>
            <strong><Copy size={15} /> Sao chép cấu hình sang trận khác</strong>
            <p style={{ color: "#94a3b8", fontSize: 13 }}>Mặc định chỉ copy nội dung. Không bao giờ copy target voter, reminder count, poll/option, incident hay lịch runtime.</p>
            <div style={{ display: "grid", gap: 6, maxHeight: 150, overflow: "auto" }}>
              {sessions.filter(item => item.id !== sessionId).map(item => <label key={item.id}><input type="checkbox" checked={copyTargets.includes(item.id)} onChange={() => setCopyTargets(cur => cur.includes(item.id) ? cur.filter(id => id !== item.id) : [...cur, item.id])} /> {item.name}</label>)}
            </div>
            <div style={{ display: "flex", gap: 12, flexWrap: "wrap", marginTop: 10 }}>
              <label><input type="checkbox" checked={copyTiming} onChange={e => setCopyTiming(e.target.checked)} /> Grace + interval</label>
              <label><input type="checkbox" checked={copyMax} onChange={e => setCopyMax(e.target.checked)} /> Max reminders</label>
              <label><input type="checkbox" checked={copySource} onChange={e => setCopySource(e.target.checked)} /> Nguồn Admin/AI</label>
            </div>
            <button type="button" disabled={busy || copyTargets.length === 0} onClick={() => void copySettings()} style={{ ...buttonStyle, marginTop: 10, background: "#475569", color: "#fff" }}><Copy size={16} /> Áp dụng cho {copyTargets.length} trận</button>
          </div>

'''
if needle not in s: raise SystemExit('frontend save anchor')
s=s.replace(needle,insert+needle,1)
# add copy function before toggleConfirm
needle2='''  function toggleConfirm(id: string) {'''
func=r'''  async function copySettings() {
    if (!token || !sessionId || copyTargets.length === 0) return;
    setBusy(true);
    try {
      const copied = await apiFetch<number>(`/sessions/${sessionId}/zalo-overbook/copy`, { method: "POST", token, body: { sourceSessionId: sessionId, targetSessionIds: copyTargets, copyMessages: true, copyTiming, copyMaxReminders: copyMax, copyMessageSource: copySource } });
      setMessage(`Đã copy cấu hình cho ${copied} trận. Runtime state không bị copy.`);
    } catch (error) { setMessage(error instanceof ApiRequestError ? error.message : "Không copy được cấu hình."); }
    finally { setBusy(false); }
  }

'''
if needle2 not in s: raise SystemExit('toggle anchor')
s=s.replace(needle2,func+needle2,1)
Path(p).write_text(s,encoding='utf-8')

# Tests
Path('server/VolleyDraft.Api.Tests/ZaloOverbookMessageCatalogTests.cs').write_text(r'''using VolleyDraft.Api.Services;
using Xunit;
namespace VolleyDraft.Api.Tests;
public sealed class ZaloOverbookMessageCatalogTests
{
    [Theory]
    [InlineData(1)] [InlineData(2)] [InlineData(3)] [InlineData(4)] [InlineData(5)] [InlineData(6)] [InlineData(7)]
    public void First_seven_reminders_have_twenty_distinct_genz_templates(int number)
    {
        var bank = ZaloOverbookMessageCatalog.GetBank(number);
        Assert.Equal(20, bank.Count);
        Assert.Equal(20, bank.Distinct(StringComparer.Ordinal).Count());
        Assert.All(bank, text => Assert.Contains("{names}", text));
    }
    [Fact] public void Reminder_100_has_fallback_bank() => Assert.Equal(20, ZaloOverbookMessageCatalog.GetBank(100).Count);
}
''',encoding='utf-8')
print('patched')