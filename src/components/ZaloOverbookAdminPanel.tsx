import { useEffect, useMemo, useState } from "react";
import { AlertTriangle, Bot, Copy, RefreshCw, Save, ShieldCheck } from "lucide-react";
import { ApiRequestError, apiFetch, type AdminSessionSummaryResponse, type PagedResponse } from "../api/dbClient";

type MessageSource = "AdminPool" | "Ai";
type StageKey = "light" | "callout" | "sarcastic" | "stubborn";

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
  reminderMessageBanks: Record<string, string[]>;
  stageMessageBanks: Record<StageKey, string[]>;
  defaultStageMessageBanks: Record<StageKey, string[]>;
  orderConfidence: string;
  needsConfirmation: boolean;
  reminderCount: number;
  lastReminderAt: string | null;
  nextReminderAt: string | null;
  currentPollId: string | null;
  currentSelectedOptionIds: string[];
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
  stageMessageBanks: Record<StageKey, string>;
  reminderMessageBanks: Record<number, string>;
};

const stageOrder: StageKey[] = ["light", "callout", "sarcastic", "stubborn"];
const stageMeta: Record<StageKey, { label: string; range: string; description: string }> = {
  light: {
    label: "Nhắc nhẹ",
    range: "Lần #1–2",
    description: "Nhẹ nhàng, vui vẻ, nhắc người vote dư tự kiểm tra và gỡ vote.",
  },
  callout: {
    label: "Réo tên",
    range: "Lần #3–5",
    description: "Réo rõ hơn vì đã nhắc vài lần nhưng vẫn giữ chất Gen Z.",
  },
  sarcastic: {
    label: "Cà khịa",
    range: "Lần #6–15",
    description: "Meme/cà khịa vui để nội dung không bị máy móc và lặp lại.",
  },
  stubborn: {
    label: "Tai trâu",
    range: "Từ lần #16",
    description: "Cà khịa mạnh hơn cho trường hợp rất lì nhưng không đe doạ hay xúc phạm nặng.",
  },
};

const emptyForm: FormState = {
  enabled: false,
  graceMinutes: 10,
  reminderIntervalMinutes: 60,
  maxReminders: 5,
  messageSource: "AdminPool",
  stageMessageBanks: { light: "", callout: "", sarcastic: "", stubborn: "" },
  reminderMessageBanks: {},
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
  const [stageEditor, setStageEditor] = useState<StageKey>("light");
  const [reminderEditor, setReminderEditor] = useState(1);
  const [copyTargets, setCopyTargets] = useState<string[]>([]);
  const [copyTiming, setCopyTiming] = useState(false);
  const [copyMax, setCopyMax] = useState(false);
  const [copySource, setCopySource] = useState(false);
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
      stageMessageBanks: {
        light: (next.stageMessageBanks?.light ?? []).join("\n"),
        callout: (next.stageMessageBanks?.callout ?? []).join("\n"),
        sarcastic: (next.stageMessageBanks?.sarcastic ?? []).join("\n"),
        stubborn: (next.stageMessageBanks?.stubborn ?? []).join("\n"),
      },
      reminderMessageBanks: Object.fromEntries(
        Object.entries(next.reminderMessageBanks ?? {}).map(([key, lines]) => [Number(key), lines.join("\n")]),
      ),
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
          friendlyMessages: [],
          seriousMessages: [],
          strictMessages: [],
          stageMessageBanks: Object.fromEntries(
            stageOrder.map((stage) => [stage, splitLines(form.stageMessageBanks[stage])]),
          ),
          reminderMessageBanks: Object.fromEntries(
            Object.entries(form.reminderMessageBanks).map(([key, value]) => [key, splitLines(value)]),
          ),
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
        body: {
          zaloUserIds: confirmIds,
          expectedPollId: status?.currentPollId ?? null,
          expectedSelectedOptionIds: status?.currentSelectedOptionIds ?? [],
        },
      });
      applyStatus(next);
      setMessage("Đã xác nhận người vote dư. Bot chỉ tag nhắc, không chuyển waitlist.");
    } catch (error) {
      setMessage(error instanceof ApiRequestError ? error.message : "Không xác nhận được lượt vote dư.");
    } finally {
      setBusy(false);
    }
  }

  async function confirmAndRemindNow() {
    if (!token || !sessionId || confirmIds.length === 0 || !status) return;
    setBusy(true);
    try {
      const next = await apiFetch<OverbookStatus>(`/sessions/${sessionId}/zalo-overbook/confirm-and-remind`, {
        method: "POST",
        token,
        body: {
          zaloUserIds: confirmIds,
          expectedPollId: status.currentPollId,
          expectedSelectedOptionIds: status.currentSelectedOptionIds,
        },
      });
      applyStatus(next);
      setMessage("Đã xác nhận và mention nhắc ngay trên Zalo. Lần gửi này được tính là reminder #1.");
    } catch (error) {
      setMessage(error instanceof ApiRequestError ? error.message : "Không gửi được cảnh báo Zalo ngay lúc này.");
    } finally {
      setBusy(false);
    }
  }

  async function copySettings() {
    if (!token || !sessionId || copyTargets.length === 0) return;
    setBusy(true);
    try {
      const copied = await apiFetch<number>(`/sessions/${sessionId}/zalo-overbook/copy`, {
        method: "POST",
        token,
        body: {
          sourceSessionId: sessionId,
          targetSessionIds: copyTargets,
          copyMessages: true,
          copyTiming,
          copyMaxReminders: copyMax,
          copyMessageSource: copySource,
        },
      });
      setMessage(`Đã copy 4 kho câu cho ${copied} trận. Runtime state không bị copy.`);
    } catch (error) {
      setMessage(error instanceof ApiRequestError ? error.message : "Không copy được cấu hình.");
    } finally {
      setBusy(false);
    }
  }

  function toggleConfirm(id: string) {
    setConfirmIds((current) => (current.includes(id) ? current.filter((item) => item !== id) : [...current, id]));
  }

  function restoreStageDefault(stage: StageKey) {
    if (!status) return;
    setForm((current) => ({
      ...current,
      stageMessageBanks: {
        ...current.stageMessageBanks,
        [stage]: (status.defaultStageMessageBanks?.[stage] ?? []).join("\n"),
      },
    }));
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

  const currentStageCount = splitLines(form.stageMessageBanks[stageEditor]).length;

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
              <input type="number" min={1} max={100} value={form.maxReminders} onChange={(event) => setForm((current) => ({ ...current, maxReminders: Number(event.target.value) }))} style={inputStyle} />
            </label>
          </div>

          <div style={{ marginTop: 18, padding: 14, borderRadius: 12, background: "rgba(30, 41, 59, 0.55)" }}>
            <strong style={{ display: "flex", alignItems: "center", gap: 8 }}><Bot size={18} /> Nguồn câu nhắc</strong>
            <div style={{ display: "flex", gap: 18, flexWrap: "wrap", marginTop: 10 }}>
              <label><input type="radio" checked={form.messageSource === "AdminPool"} onChange={() => setForm((current) => ({ ...current, messageSource: "AdminPool" }))} /> Kho câu Gen Z / admin</label>
              <label><input type="radio" checked={form.messageSource === "Ai"} onChange={() => setForm((current) => ({ ...current, messageSource: "Ai" }))} /> AI tự viết ngẫu nhiên</label>
            </div>
            {form.messageSource === "Ai" ? (
              <p style={{ color: "#94a3b8", marginBottom: 0 }}>
                AI cũng tự tăng tone theo số lần nhắc. Nếu AI lỗi/hết quota, bot dùng câu dự phòng hệ thống.
              </p>
            ) : null}
          </div>

          {form.messageSource === "AdminPool" ? (
            <div style={{ marginTop: 16, padding: 14, borderRadius: 12, background: "rgba(15, 23, 42, 0.72)" }}>
              <strong>🎭 Kho câu cảnh báo</strong>
              <p style={{ color: "#94a3b8", fontSize: 13, marginBottom: 12 }}>
                Bạn không cần quản lý từng lần #1, #2, #3 nữa. Bot tự chuyển kho: #1–2 nhắc nhẹ → #3–5 réo tên → #6–15 cà khịa → #16+ tai trâu. Trong mỗi kho bot random theo kiểu shuffle-bag để hạn chế lặp câu.
              </p>

              <div style={{ display: "grid", gridTemplateColumns: "repeat(auto-fit, minmax(145px, 1fr))", gap: 8 }}>
                {stageOrder.map((stage) => {
                  const count = splitLines(form.stageMessageBanks[stage]).length;
                  const active = stageEditor === stage;
                  return (
                    <button
                      key={stage}
                      type="button"
                      onClick={() => setStageEditor(stage)}
                      style={{
                        ...buttonStyle,
                        display: "grid",
                        textAlign: "left",
                        background: active ? "#334155" : "rgba(30, 41, 59, 0.65)",
                        color: "#fff",
                        border: active ? "1px solid rgba(96, 165, 250, 0.65)" : "1px solid rgba(148, 163, 184, 0.2)",
                      }}
                    >
                      <span>{stageMeta[stage].label}</span>
                      <small style={{ color: "#94a3b8" }}>{stageMeta[stage].range} · {count} câu</small>
                    </button>
                  );
                })}
              </div>

              <div style={{ marginTop: 14, padding: 12, borderRadius: 10, background: "rgba(30, 41, 59, 0.55)" }}>
                <div style={{ display: "flex", justifyContent: "space-between", gap: 10, flexWrap: "wrap", alignItems: "center" }}>
                  <div>
                    <strong>{stageMeta[stageEditor].label} · {stageMeta[stageEditor].range}</strong>
                    <div style={{ color: "#94a3b8", fontSize: 13, marginTop: 3 }}>{stageMeta[stageEditor].description}</div>
                  </div>
                  <span style={{ color: "#93c5fd", fontSize: 13 }}>{currentStageCount} câu</span>
                </div>
                <textarea
                  rows={12}
                  value={form.stageMessageBanks[stageEditor]}
                  onChange={(event) => setForm((current) => ({
                    ...current,
                    stageMessageBanks: { ...current.stageMessageBanks, [stageEditor]: event.target.value },
                  }))}
                  placeholder="Mỗi dòng là 1 câu. Bot sẽ random và hạn chế lặp."
                  style={{ ...inputStyle, marginTop: 10, resize: "vertical", minHeight: 220 }}
                />
                <div style={{ display: "flex", gap: 10, flexWrap: "wrap", alignItems: "center", marginTop: 10 }}>
                  <button
                    type="button"
                    onClick={() => restoreStageDefault(stageEditor)}
                    style={{ ...buttonStyle, background: "#334155", color: "#fff" }}
                  >
                    Khôi phục kho Gen Z mặc định
                  </button>
                  <span style={{ color: "#94a3b8", fontSize: 12 }}>
                    Mặc định hệ thống: Nhắc nhẹ 50 · Réo tên 50 · Cà khịa 100 · Tai trâu 100 câu.
                  </span>
                </div>
                <div style={{ color: "#94a3b8", fontSize: 12, marginTop: 8 }}>
                  Placeholder: {'{names}'} {'{capacity}'} {'{firstExcessSlot}'} {'{effectiveSlotCount}'} {'{rawVoterCount}'} {'{excessCount}'} {'{reminderNumber}'} {'{sessionName}'}
                </div>
              </div>

              <details style={{ marginTop: 12 }}>
                <summary style={{ cursor: "pointer", color: "#cbd5e1" }}>Nâng cao · override riêng một lần nhắc</summary>
                <p style={{ color: "#94a3b8", fontSize: 13 }}>
                  Chỉ dùng khi bạn thật sự muốn một lần cụ thể, ví dụ #10, có kho riêng. Nếu để trống thì bot dùng 4 kho lớn phía trên.
                </p>
                <div style={{ display: "flex", gap: 8, flexWrap: "wrap", alignItems: "center" }}>
                  <label>
                    Lần #{" "}
                    <input
                      type="number"
                      min={1}
                      max={100}
                      value={reminderEditor}
                      onChange={(event) => setReminderEditor(Math.max(1, Math.min(100, Number(event.target.value) || 1)))}
                      style={{ ...inputStyle, width: 90, display: "inline-block" }}
                    />
                  </label>
                </div>
                <textarea
                  rows={5}
                  value={form.reminderMessageBanks[reminderEditor] ?? ""}
                  onChange={(event) => setForm((current) => ({
                    ...current,
                    reminderMessageBanks: { ...current.reminderMessageBanks, [reminderEditor]: event.target.value },
                  }))}
                  placeholder="Để trống để dùng kho theo giai đoạn. Mỗi dòng là một câu override."
                  style={{ ...inputStyle, marginTop: 10, resize: "vertical" }}
                />
              </details>
            </div>
          ) : null}

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
              <div style={{ display: "flex", gap: 10, flexWrap: "wrap", marginTop: 12 }}>
                <button type="button" onClick={() => void confirmTargets()} disabled={busy || confirmIds.length === 0} style={{ ...buttonStyle, background: "#f59e0b", color: "#111827" }}>
                  <ShieldCheck size={16} /> Xác nhận người vote dư
                </button>
                <button
                  type="button"
                  onClick={() => void confirmAndRemindNow()}
                  disabled={busy || confirmIds.length === 0 || !status.botEnabled || !status.enabled}
                  style={{ ...buttonStyle, background: "#ef4444", color: "#fff", opacity: !status.botEnabled || !status.enabled ? 0.55 : 1 }}
                >
                  <Bot size={16} /> Xác nhận & nhắc ngay
                </button>
              </div>
              {!status.enabled || !status.botEnabled ? (
                <p style={{ marginBottom: 0, color: "#fbbf24", fontSize: 13 }}>Muốn nhắc ngay, hãy bật bot Zalo và bật + lưu cảnh báo vượt slot trước. Sau lần gửi ngay, bot tiếp tục dùng khoảng cách nhắc đã cấu hình.</p>
              ) : null}
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

          <div style={{ marginTop: 16, padding: 14, borderRadius: 12, background: "rgba(15, 23, 42, 0.72)" }}>
            <strong><Copy size={15} /> Sao chép cấu hình sang trận khác</strong>
            <p style={{ color: "#94a3b8", fontSize: 13 }}>
              Mặc định copy toàn bộ 4 kho câu và các override nâng cao. Không bao giờ copy target voter, reminder count, poll/option, incident hay lịch runtime.
            </p>
            <div style={{ display: "grid", gap: 6, maxHeight: 150, overflow: "auto" }}>
              {sessions.filter((item) => item.id !== sessionId).map((item) => (
                <label key={item.id}>
                  <input
                    type="checkbox"
                    checked={copyTargets.includes(item.id)}
                    onChange={() => setCopyTargets((current) => current.includes(item.id) ? current.filter((id) => id !== item.id) : [...current, item.id])}
                  />{" "}
                  {item.name}
                </label>
              ))}
            </div>
            <div style={{ display: "flex", gap: 12, flexWrap: "wrap", marginTop: 10 }}>
              <label><input type="checkbox" checked={copyTiming} onChange={(event) => setCopyTiming(event.target.checked)} /> Grace + interval</label>
              <label><input type="checkbox" checked={copyMax} onChange={(event) => setCopyMax(event.target.checked)} /> Max reminders</label>
              <label><input type="checkbox" checked={copySource} onChange={(event) => setCopySource(event.target.checked)} /> Nguồn Admin/AI</label>
            </div>
            <button type="button" disabled={busy || copyTargets.length === 0} onClick={() => void copySettings()} style={{ ...buttonStyle, marginTop: 10, background: "#475569", color: "#fff" }}>
              <Copy size={16} /> Áp dụng cho {copyTargets.length} trận
            </button>
          </div>

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
