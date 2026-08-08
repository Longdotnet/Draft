import { useEffect, useMemo, useState } from "react";
import { AlertTriangle, Bot, RefreshCw, Save, ShieldCheck } from "lucide-react";
import { ApiRequestError, apiFetch, type AdminSessionSummaryResponse, type PagedResponse } from "../api/dbClient";

type MessageSource = "AdminPool" | "Ai";

type OverbookVoter = {
  zaloUserId: string;
  displayName: string;
  votePosition: number;
  suggestedExcess: boolean;
  confirmedExcess: boolean;
  isSharedSlotMember: boolean;
};

type OverbookStatus = {
  sessionId: string;
  sessionName: string;
  zaloGroupName: string | null;
  botEnabled: boolean;
  enabled: boolean;
  capacity: number;
  effectiveSlotCount: number;
  rawVoterCount: number;
  excessSlotCount: number;
  graceMinutes: number;
  reminderIntervalMinutes: number;
  maxReminders: number;
  messageSource: MessageSource;
  friendlyMessages: string[];
  seriousMessages: string[];
  strictMessages: string[];
  orderConfidence: string;
  needsConfirmation: boolean;
  reminderCount: number;
  lastReminderAt: string | null;
  nextReminderAt: string | null;
  voters: OverbookVoter[];
  currentTargetZaloUserIds: string[];
  lastError: string | null;
};

type FormState = {
  enabled: boolean;
  graceMinutes: number;
  reminderIntervalMinutes: number;
  maxReminders: number;
  messageSource: MessageSource;
  friendlyMessages: string;
  seriousMessages: string;
  strictMessages: string;
};

const emptyForm: FormState = {
  enabled: false,
  graceMinutes: 10,
  reminderIntervalMinutes: 60,
  maxReminders: 5,
  messageSource: "AdminPool",
  friendlyMessages: "",
  seriousMessages: "",
  strictMessages: "",
};

function splitLines(value: string) {
  return value
    .split("\n")
    .map((line) => line.trim())
    .filter(Boolean);
}

function formatTime(value: string | null) {
  if (!value) return "—";
  return new Intl.DateTimeFormat("vi-VN", {
    day: "2-digit",
    month: "2-digit",
    hour: "2-digit",
    minute: "2-digit",
  }).format(new Date(value));
}

function confidenceText(value: string) {
  const labels: Record<string, string> = {
    Unknown: "Chưa có dữ liệu thứ tự",
    ObservedWithinCapacity: "Đã quan sát khi poll chưa đầy",
    MultiOptionWithinCapacity: "Nhiều option - chưa cần xác định lượt dư",
    ObservedLive: "Quan sát được lượt vote mới từ Zalo",
    AdminConfirmed: "Admin đã xác nhận",
    InitialSnapshotOverCapacity: "Poll đã vượt slot ngay lúc bắt đầu theo dõi",
    MultipleOptionsUncertain: "Đang gộp nhiều option nên không chắc thứ tự chung",
    OrderChangedUncertain: "Thứ tự voter thay đổi bất thường",
    NonPollCapacityConflict: "Roster ngoài poll đã chiếm quá nhiều slot",
    TargetsNoLongerPresent: "Người đã xác nhận không còn trong poll",
  };
  return labels[value] ?? value;
}

export function ZaloOverbookAdminPanel() {
  const [token, setToken] = useState(() => localStorage.getItem("volleyDraftToken"));
  const [sessions, setSessions] = useState<AdminSessionSummaryResponse[]>([]);
  const [sessionId, setSessionId] = useState("");
  const [status, setStatus] = useState<OverbookStatus | null>(null);
  const [form, setForm] = useState<FormState>(emptyForm);
  const [confirmIds, setConfirmIds] = useState<string[]>([]);
  const [busy, setBusy] = useState(false);
  const [message, setMessage] = useState<string | null>(null);

  useEffect(() => {
    const timer = window.setInterval(() => {
      setToken((current) => {
        const next = localStorage.getItem("volleyDraftToken");
        return current === next ? current : next;
      });
    }, 800);
    return () => window.clearInterval(timer);
  }, []);

  useEffect(() => {
    if (!token) {
      setSessions([]);
      setSessionId("");
      setStatus(null);
      return;
    }
    void loadSessions(token);
  }, [token]);

  useEffect(() => {
    if (!token || !sessionId) {
      setStatus(null);
      return;
    }
    void loadStatus(token, sessionId);
  }, [token, sessionId]);

  const displayedVoters = useMemo(() => {
    if (!status) return [];
    const important = status.voters.filter(
      (voter) => voter.suggestedExcess || voter.confirmedExcess || status.currentTargetZaloUserIds.includes(voter.zaloUserId),
    );
    const tail = status.voters.slice(-Math.max(8, status.excessSlotCount + 4));
    const ids = new Set(important.map((voter) => voter.zaloUserId));
    return [...important, ...tail.filter((voter) => !ids.has(voter.zaloUserId))].sort(
      (left, right) => left.votePosition - right.votePosition,
    );
  }, [status]);

  if (!token) return null;

  async function loadSessions(authToken: string) {
    try {
      const page = await apiFetch<PagedResponse<AdminSessionSummaryResponse>>("/sessions?page=1&pageSize=30", {
        token: authToken,
      });
      const usable = page.items.filter((item) => item.status !== "Cancelled" && item.status !== "Finished");
      setSessions(usable);
      setSessionId((current) => (usable.some((item) => item.id === current) ? current : usable[0]?.id ?? ""));
    } catch (error) {
      setMessage(error instanceof ApiRequestError ? error.message : "Không tải được danh sách trận.");
    }
  }

  async function loadStatus(authToken = token, id = sessionId) {
    if (!authToken || !id) return;
    setBusy(true);
    try {
      const next = await apiFetch<OverbookStatus>(`/sessions/${id}/zalo-overbook`, { token: authToken });
      applyStatus(next);
      setMessage(null);
    } catch (error) {
      setStatus(null);
      setMessage(error instanceof ApiRequestError ? error.message : "Không đọc được cảnh báo vượt slot.");
    } finally {
      setBusy(false);
    }
  }

  function applyStatus(next: OverbookStatus) {
    setStatus(next);
    setForm({
      enabled: next.enabled,
      graceMinutes: next.graceMinutes,
      reminderIntervalMinutes: next.reminderIntervalMinutes,
      maxReminders: next.maxReminders,
      messageSource: next.messageSource,
      friendlyMessages: next.friendlyMessages.join("\n"),
      seriousMessages: next.seriousMessages.join("\n"),
      strictMessages: next.strictMessages.join("\n"),
    });
    setConfirmIds(next.voters.filter((voter) => voter.suggestedExcess).map((voter) => voter.zaloUserId));
  }

  async function saveSettings() {
    if (!token || !sessionId) return;
    setBusy(true);
    try {
      const next = await apiFetch<OverbookStatus>(`/sessions/${sessionId}/zalo-overbook`, {
        method: "PUT",
        token,
        body: {
          enabled: form.enabled,
          graceMinutes: Number(form.graceMinutes),
          reminderIntervalMinutes: Number(form.reminderIntervalMinutes),
          maxReminders: Number(form.maxReminders),
          messageSource: form.messageSource,
          friendlyMessages: splitLines(form.friendlyMessages),
          seriousMessages: splitLines(form.seriousMessages),
          strictMessages: splitLines(form.strictMessages),
        },
      });
      applyStatus(next);
      setMessage("Đã lưu cấu hình cảnh báo vượt slot.");
    } catch (error) {
      setMessage(error instanceof ApiRequestError ? error.message : "Không lưu được cấu hình.");
    } finally {
      setBusy(false);
    }
  }

  async function confirmTargets() {
    if (!token || !sessionId || confirmIds.length === 0) return;
    setBusy(true);
    try {
      const next = await apiFetch<OverbookStatus>(`/sessions/${sessionId}/zalo-overbook/confirm`, {
        method: "POST",
        token,
        body: { zaloUserIds: confirmIds },
      });
      applyStatus(next);
      setMessage("Đã xác nhận người vote dư. Bot chỉ tag nhắc, không chuyển waitlist.");
    } catch (error) {
      setMessage(error instanceof ApiRequestError ? error.message : "Không xác nhận được lượt vote dư.");
    } finally {
      setBusy(false);
    }
  }

  function toggleConfirm(id: string) {
    setConfirmIds((current) => (current.includes(id) ? current.filter((item) => item !== id) : [...current, id]));
  }

  const cardStyle = {
    margin: "24px auto 0",
    maxWidth: 1180,
    padding: 20,
    borderRadius: 18,
    border: "1px solid rgba(148, 163, 184, 0.22)",
    background: "rgba(15, 23, 42, 0.72)",
    color: "#e2e8f0",
  } as const;
  const gridStyle = {
    display: "grid",
    gridTemplateColumns: "repeat(auto-fit, minmax(220px, 1fr))",
    gap: 12,
  } as const;
  const inputStyle = {
    width: "100%",
    padding: "10px 12px",
    borderRadius: 10,
    border: "1px solid rgba(148, 163, 184, 0.35)",
    background: "rgba(15, 23, 42, 0.9)",
    color: "#f8fafc",
  } as const;
  const buttonStyle = {
    display: "inline-flex",
    alignItems: "center",
    gap: 7,
    border: 0,
    borderRadius: 10,
    padding: "10px 14px",
    cursor: busy ? "wait" : "pointer",
    fontWeight: 700,
  } as const;

  return (
    <section style={cardStyle} aria-label="Cảnh báo vượt slot Zalo">
      <div style={{ display: "flex", alignItems: "center", justifyContent: "space-between", gap: 12, flexWrap: "wrap" }}>
        <div>
          <p style={{ margin: 0, color: "#94a3b8", fontSize: 13 }}>Zalo poll guard</p>
          <h2 style={{ margin: "4px 0 0", display: "flex", alignItems: "center", gap: 8 }}>
            <AlertTriangle size={22} /> Cảnh báo vote vượt slot
          </h2>
          <p style={{ margin: "8px 0 0", color: "#94a3b8", maxWidth: 720 }}>
            Chỉ tag người nằm ngoài capacity. Tính năng này không tự xoá vote, không đưa người vào waitlist và không đổi roster.
          </p>
        </div>
        <button type="button" onClick={() => void loadStatus()} disabled={busy || !sessionId} style={{ ...buttonStyle, background: "#334155", color: "#fff" }}>
          <RefreshCw size={16} /> Đồng bộ trạng thái
        </button>
      </div>

      <div style={{ marginTop: 16 }}>
        <label style={{ display: "grid", gap: 6 }}>
          <span>Trận đang cấu hình</span>
          <select value={sessionId} onChange={(event) => setSessionId(event.target.value)} style={inputStyle}>
            {sessions.length === 0 ? <option value="">Chưa có trận khả dụng</option> : null}
            {sessions.map((session) => (
              <option key={session.id} value={session.id}>
                {session.name} · {session.playerCount}/{session.requiredPlayerCount}
              </option>
            ))}
          </select>
        </label>
      </div>

      {status ? (
        <>
          <div style={{ ...gridStyle, marginTop: 16 }}>
            <div style={{ padding: 14, borderRadius: 12, background: "rgba(30, 41, 59, 0.7)" }}>
              <strong>{status.effectiveSlotCount}/{status.capacity} slot hiệu lực</strong>
              <div style={{ color: "#94a3b8", marginTop: 4 }}>{status.rawVoterCount} voter · dư {status.excessSlotCount} slot</div>
            </div>
            <div style={{ padding: 14, borderRadius: 12, background: "rgba(30, 41, 59, 0.7)" }}>
              <strong>{confidenceText(status.orderConfidence)}</strong>
              <div style={{ color: "#94a3b8", marginTop: 4 }}>{status.needsConfirmation ? "Cần admin xác nhận trước khi tag" : "Có thể dùng trạng thái hiện tại"}</div>
            </div>
            <div style={{ padding: 14, borderRadius: 12, background: "rgba(30, 41, 59, 0.7)" }}>
              <strong>Đã nhắc {status.reminderCount}/{status.maxReminders} lần</strong>
              <div style={{ color: "#94a3b8", marginTop: 4 }}>Lần tới: {formatTime(status.nextReminderAt)}</div>
            </div>
          </div>

          {!status.botEnabled ? (
            <div style={{ marginTop: 14, padding: 12, borderRadius: 10, background: "rgba(245, 158, 11, 0.13)", color: "#fbbf24" }}>
              Bot của trận đang tắt. Bật bot Zalo trước thì cảnh báo mới gửi được.
            </div>
          ) : null}
          {status.lastError ? (
            <div style={{ marginTop: 14, padding: 12, borderRadius: 10, background: "rgba(239, 68, 68, 0.12)", color: "#fca5a5" }}>
              {status.lastError}
            </div>
          ) : null}

          <div style={{ ...gridStyle, marginTop: 18 }}>
            <label style={{ display: "flex", alignItems: "center", gap: 9, padding: 12, borderRadius: 10, background: "rgba(30, 41, 59, 0.55)" }}>
              <input type="checkbox" checked={form.enabled} onChange={(event) => setForm((current) => ({ ...current, enabled: event.target.checked }))} />
              <span><strong>Bật cảnh báo vượt slot</strong><br /><small style={{ color: "#94a3b8" }}>Mặc định tắt cho các trận cũ</small></span>
            </label>
            <label style={{ display: "grid", gap: 6 }}>
              <span>Chờ lần đầu (phút)</span>
              <input type="number" min={0} max={1440} value={form.graceMinutes} onChange={(event) => setForm((current) => ({ ...current, graceMinutes: Number(event.target.value) }))} style={inputStyle} />
            </label>
            <label style={{ display: "grid", gap: 6 }}>
              <span>Khoảng cách nhắc (phút)</span>
              <input type="number" min={5} max={10080} value={form.reminderIntervalMinutes} onChange={(event) => setForm((current) => ({ ...current, reminderIntervalMinutes: Number(event.target.value) }))} style={inputStyle} />
            </label>
            <label style={{ display: "grid", gap: 6 }}>
              <span>Số lần nhắc tối đa</span>
              <input type="number" min={1} max={20} value={form.maxReminders} onChange={(event) => setForm((current) => ({ ...current, maxReminders: Number(event.target.value) }))} style={inputStyle} />
            </label>
          </div>

          <div style={{ marginTop: 18, padding: 14, borderRadius: 12, background: "rgba(30, 41, 59, 0.55)" }}>
            <strong style={{ display: "flex", alignItems: "center", gap: 8 }}><Bot size={18} /> Nguồn câu nhắc</strong>
            <div style={{ display: "flex", gap: 18, flexWrap: "wrap", marginTop: 10 }}>
              <label><input type="radio" checked={form.messageSource === "AdminPool"} onChange={() => setForm((current) => ({ ...current, messageSource: "AdminPool" }))} /> Kho câu của admin</label>
              <label><input type="radio" checked={form.messageSource === "Ai"} onChange={() => setForm((current) => ({ ...current, messageSource: "Ai" }))} /> AI tự viết ngẫu nhiên</label>
            </div>
            {form.messageSource === "Ai" ? (
              <p style={{ color: "#94a3b8", marginBottom: 0 }}>AI: lần 1–2 nhẹ, 3–4 nghiêm túc, từ lần 5 cứng rắn. Nếu AI lỗi/hết quota, bot dùng câu dự phòng hệ thống.</p>
            ) : (
              <div style={{ ...gridStyle, marginTop: 14 }}>
                <label style={{ display: "grid", gap: 6 }}>
                  <span>Lần 1–2 · nhẹ</span>
                  <textarea rows={6} value={form.friendlyMessages} onChange={(event) => setForm((current) => ({ ...current, friendlyMessages: event.target.value }))} placeholder="Mỗi dòng là một câu. Bot dùng hết kho rồi mới lặp." style={inputStyle} />
                </label>
                <label style={{ display: "grid", gap: 6 }}>
                  <span>Lần 3–4 · nghiêm túc</span>
                  <textarea rows={6} value={form.seriousMessages} onChange={(event) => setForm((current) => ({ ...current, seriousMessages: event.target.value }))} placeholder="Có thể dùng {sessionName}, {effectiveSlotCount}, {capacity}, {excessCount}, {reminderNumber}, {names}." style={inputStyle} />
                </label>
                <label style={{ display: "grid", gap: 6 }}>
                  <span>Từ lần 5 · cứng/cà khịa theo văn phong nhóm</span>
                  <textarea rows={6} value={form.strictMessages} onChange={(event) => setForm((current) => ({ ...current, strictMessages: event.target.value }))} placeholder="Admin tự kiểm soát văn phong. Backend vẫn giữ đúng số slot và người được tag." style={inputStyle} />
                </label>
              </div>
            )}
          </div>

          {status.needsConfirmation && status.excessSlotCount > 0 ? (
            <div style={{ marginTop: 18, padding: 14, borderRadius: 12, border: "1px solid rgba(245, 158, 11, 0.45)", background: "rgba(245, 158, 11, 0.08)" }}>
              <strong style={{ display: "flex", alignItems: "center", gap: 8, color: "#fbbf24" }}><ShieldCheck size={18} /> Cần xác nhận lượt vote dư</strong>
              <p style={{ color: "#cbd5e1" }}>
                Hệ thống không đủ bằng chứng để tự khẳng định ai là lượt #{status.capacity + 1} trở đi. Các ô được tick sẵn chỉ là gợi ý theo thứ tự voter Zalo đang trả về.
              </p>
              <div style={{ display: "grid", gap: 7 }}>
                {displayedVoters.map((voter) => (
                  <label key={voter.zaloUserId} style={{ display: "flex", alignItems: "center", gap: 9, padding: "7px 9px", borderRadius: 8, background: voter.suggestedExcess ? "rgba(245, 158, 11, 0.12)" : "rgba(15, 23, 42, 0.45)" }}>
                    <input type="checkbox" checked={confirmIds.includes(voter.zaloUserId)} onChange={() => toggleConfirm(voter.zaloUserId)} />
                    <span>#{voter.votePosition} · {voter.displayName}</span>
                    {voter.isSharedSlotMember ? <small style={{ color: "#a78bfa" }}>shared slot</small> : null}
                    {voter.suggestedExcess ? <small style={{ color: "#fbbf24" }}>gợi ý lượt dư</small> : null}
                  </label>
                ))}
              </div>
              <button type="button" onClick={() => void confirmTargets()} disabled={busy || confirmIds.length === 0} style={{ ...buttonStyle, marginTop: 12, background: "#f59e0b", color: "#111827" }}>
                <ShieldCheck size={16} /> Xác nhận người vote dư
              </button>
              <p style={{ marginBottom: 0, color: "#94a3b8", fontSize: 13 }}>Ngoài website, admin/operator có thể mention bot và gõ “xác nhận vote dư” khi group chỉ có một trận đang chờ xác nhận.</p>
            </div>
          ) : null}

          {!status.needsConfirmation && status.currentTargetZaloUserIds.length > 0 ? (
            <div style={{ marginTop: 18, padding: 14, borderRadius: 12, background: "rgba(16, 185, 129, 0.08)" }}>
              <strong>Đang tag nhắc:</strong>{" "}
              {status.voters
                .filter((voter) => status.currentTargetZaloUserIds.includes(voter.zaloUserId))
                .map((voter) => `#${voter.votePosition} ${voter.displayName}`)
                .join(", ")}
            </div>
          ) : null}

          <div style={{ display: "flex", alignItems: "center", gap: 10, flexWrap: "wrap", marginTop: 18 }}>
            <button type="button" onClick={() => void saveSettings()} disabled={busy || !sessionId} style={{ ...buttonStyle, background: "#22c55e", color: "#052e16" }}>
              <Save size={16} /> Lưu cấu hình
            </button>
            <span style={{ color: "#94a3b8", fontSize: 13 }}>Scheduler chạy theo nhịp backend; lúc Render ngủ có thể trễ thêm theo lần đánh thức kế tiếp.</span>
          </div>
        </>
      ) : null}

      {message ? <div style={{ marginTop: 12, color: message.startsWith("Đã") ? "#86efac" : "#fca5a5" }}>{message}</div> : null}
    </section>
  );
}
