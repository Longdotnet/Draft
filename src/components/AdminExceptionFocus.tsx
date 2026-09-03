import { FormEvent, useEffect, useMemo, useState } from "react";
import { AlertTriangle, ArrowLeft, CheckCircle2, LogIn, RefreshCw, ShieldCheck } from "lucide-react";
import {
  ApiRequestError,
  apiFetch,
  type AuthResponse,
  type SessionResponse,
} from "../api/dbClient";
import { ZaloPollImportPanel } from "./ZaloPollImportPanel";

type ExceptionFocus = "bot-overbook-control" | "auto-session-control" | "draft-workspace";
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
  enabled: boolean;
  capacity: number;
  effectiveSlotCount: number;
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
  orderConfidence: string;
  needsConfirmation: boolean;
  currentPollId: string | null;
  currentSelectedOptionIds: string[];
  voters: OverbookVoter[];
  currentTargetZaloUserIds: string[];
  lastError: string | null;
};

const focusLabels: Record<ExceptionFocus, string> = {
  "bot-overbook-control": "Xác nhận dư slot",
  "auto-session-control": "Sửa liên kết Zalo / Auto Session",
  "draft-workspace": "Kiểm tra dữ liệu session",
};

function cardStyle() {
  return {
    width: "min(100%, 760px)",
    margin: "0 auto",
    padding: 18,
    borderRadius: 18,
    border: "1px solid rgba(148, 163, 184, .28)",
    background: "rgba(15, 23, 42, .86)",
    color: "#e2e8f0",
    boxSizing: "border-box" as const,
  };
}

export function AdminExceptionFocus({
  focus,
  sessionId,
}: {
  focus: ExceptionFocus;
  sessionId: string;
}) {
  const [token, setToken] = useState(() => localStorage.getItem("volleyDraftToken"));
  const [email, setEmail] = useState("");
  const [password, setPassword] = useState("");
  const [busy, setBusy] = useState(false);
  const [message, setMessage] = useState<string | null>(null);

  useEffect(() => {
    const timer = window.setInterval(() => {
      const next = localStorage.getItem("volleyDraftToken");
      setToken((current) => (current === next ? current : next));
    }, 600);
    return () => window.clearInterval(timer);
  }, []);

  async function login(event: FormEvent<HTMLFormElement>) {
    event.preventDefault();
    if (!email.trim() || !password) return;
    setBusy(true);
    setMessage(null);
    try {
      const response = await apiFetch<AuthResponse>("/auth/login", {
        method: "POST",
        body: { email: email.trim(), password },
      });
      localStorage.setItem("volleyDraftToken", response.token);
      localStorage.setItem("volleyDraftUser", JSON.stringify(response.user));
      setToken(response.token);
    } catch (error) {
      setMessage(error instanceof Error ? error.message : "Không đăng nhập được.");
    } finally {
      setBusy(false);
    }
  }

  return (
    <section style={{ padding: "16px 12px 40px" }}>
      <div style={cardStyle()}>
        <div style={{ display: "flex", alignItems: "flex-start", gap: 12 }}>
          <AlertTriangle size={24} aria-hidden="true" />
          <div style={{ minWidth: 0, flex: 1 }}>
            <div style={{ fontSize: 12, opacity: 0.72, textTransform: "uppercase", letterSpacing: ".08em" }}>
              Match Brief · Exception Portal
            </div>
            <h2 style={{ margin: "4px 0 6px", fontSize: 22 }}>{focusLabels[focus]}</h2>
            <p style={{ margin: 0, lineHeight: 1.5 }}>
              Bot đã đưa ông tới đúng lỗi của session này. Chỉ xử lý phần bên dưới; xong thì quay lại Zalo, không cần dò cả website.
            </p>
          </div>
        </div>
      </div>

      {!token ? (
        <form onSubmit={login} style={{ ...cardStyle(), marginTop: 12 }}>
          <div style={{ display: "flex", gap: 9, alignItems: "center", marginBottom: 12 }}>
            <ShieldCheck size={20} aria-hidden="true" />
            <strong>Cần đăng nhập admin để xử lý exception</strong>
          </div>
          <label style={{ display: "grid", gap: 6, marginBottom: 10 }}>
            <span>Email</span>
            <input
              type="email"
              autoComplete="username"
              value={email}
              onChange={(event) => setEmail(event.target.value)}
              style={{ padding: 12, borderRadius: 10, border: "1px solid #475569", fontSize: 16 }}
            />
          </label>
          <label style={{ display: "grid", gap: 6, marginBottom: 12 }}>
            <span>Mật khẩu</span>
            <input
              type="password"
              autoComplete="current-password"
              value={password}
              onChange={(event) => setPassword(event.target.value)}
              style={{ padding: 12, borderRadius: 10, border: "1px solid #475569", fontSize: 16 }}
            />
          </label>
          {message ? <p role="alert">{message}</p> : null}
          <button type="submit" disabled={busy} style={{ padding: "11px 14px", borderRadius: 10, fontWeight: 700 }}>
            <LogIn size={17} style={{ verticalAlign: "middle", marginRight: 7 }} aria-hidden="true" />
            {busy ? "Đang đăng nhập…" : "Đăng nhập và mở đúng exception"}
          </button>
        </form>
      ) : focus === "bot-overbook-control" ? (
        <FocusedOverbookException token={token} sessionId={sessionId} />
      ) : (
        <FocusedSessionException token={token} sessionId={sessionId} />
      )}

      <div style={{ width: "min(100%, 760px)", margin: "14px auto 0", opacity: 0.76, fontSize: 14 }}>
        <ArrowLeft size={15} style={{ verticalAlign: "middle", marginRight: 5 }} aria-hidden="true" />
        Sau khi xử lý xong, quay lại Zalo. Bot sẽ đọc state mới ở lượt tiếp theo.
      </div>
    </section>
  );
}

function FocusedSessionException({ token, sessionId }: { token: string; sessionId: string }) {
  const [session, setSession] = useState<SessionResponse | null>(null);
  const [error, setError] = useState<string | null>(null);
  const [busy, setBusy] = useState(true);

  async function load() {
    setBusy(true);
    setError(null);
    try {
      setSession(await apiFetch<SessionResponse>(`/sessions/${encodeURIComponent(sessionId)}`, { token }));
    } catch (caught) {
      setError(caught instanceof Error ? caught.message : "Không mở được session.");
    } finally {
      setBusy(false);
    }
  }

  useEffect(() => { void load(); }, [token, sessionId]);

  if (busy && !session) return <StatusCard text="Đang mở đúng session…" />;
  if (error) return <StatusCard text={error} danger onRetry={() => void load()} />;
  if (!session) return null;

  return (
    <div style={{ width: "min(100%, 1180px)", margin: "12px auto 0" }}>
      <div style={cardStyle()}>
        <strong>{session.name}</strong>
        <div style={{ marginTop: 6, opacity: 0.8 }}>
          Trạng thái: {session.status} · {session.teamCount} team × {session.teamSize} · {session.startTime ? new Date(session.startTime).toLocaleString("vi-VN") : "chưa có giờ"}
        </div>
      </div>
      <ZaloPollImportPanel
        token={token}
        session={session}
        onSessionUpdated={setSession}
        onImported={async () => { await load(); }}
      />
    </div>
  );
}

function FocusedOverbookException({ token, sessionId }: { token: string; sessionId: string }) {
  const [status, setStatus] = useState<OverbookStatus | null>(null);
  const [selectedIds, setSelectedIds] = useState<string[]>([]);
  const [busy, setBusy] = useState(true);
  const [message, setMessage] = useState<string | null>(null);

  const candidates = useMemo(
    () => status?.voters.filter((voter) => voter.suggestedExcess || voter.confirmedExcess || status.currentTargetZaloUserIds.includes(voter.zaloUserId)) ?? [],
    [status],
  );

  async function load() {
    setBusy(true);
    setMessage(null);
    try {
      const next = await apiFetch<OverbookStatus>(`/sessions/${encodeURIComponent(sessionId)}/zalo-overbook`, { token });
      setStatus(next);
      setSelectedIds(next.voters.filter((voter) => voter.suggestedExcess).map((voter) => voter.zaloUserId));
    } catch (error) {
      setMessage(error instanceof ApiRequestError ? error.message : "Không đọc được Overbook state.");
    } finally {
      setBusy(false);
    }
  }

  useEffect(() => { void load(); }, [token, sessionId]);

  async function enableAutomation() {
    if (!status) return;
    setBusy(true);
    try {
      const next = await apiFetch<OverbookStatus>(`/sessions/${encodeURIComponent(sessionId)}/zalo-overbook`, {
        method: "PUT",
        token,
        body: {
          enabled: true,
          graceMinutes: status.graceMinutes,
          reminderIntervalMinutes: status.reminderIntervalMinutes,
          maxReminders: status.maxReminders,
          messageSource: status.messageSource,
          friendlyMessages: status.friendlyMessages,
          seriousMessages: status.seriousMessages,
          strictMessages: status.strictMessages,
          stageMessageBanks: status.stageMessageBanks,
          reminderMessageBanks: status.reminderMessageBanks,
        },
      });
      setStatus(next);
      setMessage("Đã bật Overbook automation cho đúng session này.");
    } catch (error) {
      setMessage(error instanceof Error ? error.message : "Không bật được Overbook.");
    } finally {
      setBusy(false);
    }
  }

  async function confirm(remindNow: boolean) {
    if (!status || selectedIds.length === 0) return;
    setBusy(true);
    try {
      const suffix = remindNow ? "confirm-and-remind" : "confirm";
      const next = await apiFetch<OverbookStatus>(`/sessions/${encodeURIComponent(sessionId)}/zalo-overbook/${suffix}`, {
        method: "POST",
        token,
        body: {
          zaloUserIds: selectedIds,
          expectedPollId: status.currentPollId,
          expectedSelectedOptionIds: status.currentSelectedOptionIds,
        },
      });
      setStatus(next);
      setSelectedIds(next.voters.filter((voter) => voter.suggestedExcess).map((voter) => voter.zaloUserId));
      setMessage(remindNow ? "Đã xác nhận và nhắc ngay trên Zalo." : "Đã xác nhận target dư slot; bot sẽ tiếp tục tự chạy.");
    } catch (error) {
      setMessage(error instanceof Error ? error.message : "Không xác nhận được target.");
    } finally {
      setBusy(false);
    }
  }

  function toggle(id: string) {
    setSelectedIds((current) => current.includes(id) ? current.filter((item) => item !== id) : [...current, id]);
  }

  if (busy && !status) return <StatusCard text="Đang refresh Overbook state…" />;
  if (!status) return <StatusCard text={message ?? "Không có dữ liệu Overbook."} danger onRetry={() => void load()} />;

  return (
    <div style={{ ...cardStyle(), marginTop: 12 }}>
      <div style={{ display: "flex", justifyContent: "space-between", gap: 10, alignItems: "center", flexWrap: "wrap" }}>
        <div>
          <h3 style={{ margin: 0 }}>{status.sessionName}</h3>
          <div style={{ marginTop: 5 }}>{status.effectiveSlotCount}/{status.capacity} effective slot · dư {status.excessSlotCount}</div>
        </div>
        <button type="button" disabled={busy} onClick={() => void load()} style={{ padding: "9px 11px", borderRadius: 9 }}>
          <RefreshCw size={16} style={{ verticalAlign: "middle", marginRight: 5 }} aria-hidden="true" /> Refresh
        </button>
      </div>

      {message ? <p role="status">{message}</p> : null}
      {status.lastError ? <p role="alert">Lỗi gần nhất: {status.lastError}</p> : null}

      {!status.enabled ? (
        <div style={{ marginTop: 14, padding: 12, borderRadius: 12, background: "rgba(245, 158, 11, .12)" }}>
          <strong>Overbook automation đang tắt.</strong>
          <p>Đây là lý do bot không thể tự tiếp tục. Bật ngay bằng cấu hình hiện tại, không đổi message/timing khác.</p>
          <button type="button" disabled={busy} onClick={() => void enableAutomation()} style={{ padding: "10px 13px", borderRadius: 9, fontWeight: 700 }}>
            Bật automation cho session này
          </button>
        </div>
      ) : null}

      {status.needsConfirmation ? (
        <div style={{ marginTop: 16 }}>
          <strong>Bot chưa đủ bằng chứng để tự chọn người dư. Xác nhận đúng target:</strong>
          <div style={{ display: "grid", gap: 8, marginTop: 10 }}>
            {candidates.map((voter) => (
              <label key={voter.zaloUserId} style={{ display: "flex", gap: 10, alignItems: "center", padding: 10, borderRadius: 10, background: "rgba(30, 41, 59, .75)" }}>
                <input type="checkbox" checked={selectedIds.includes(voter.zaloUserId)} onChange={() => toggle(voter.zaloUserId)} />
                <span style={{ flex: 1 }}>{voter.displayName}</span>
                <small>#{voter.votePosition}{voter.suggestedExcess ? " · bot đề xuất" : ""}</small>
              </label>
            ))}
          </div>
          <div style={{ display: "flex", gap: 8, flexWrap: "wrap", marginTop: 12 }}>
            <button type="button" disabled={busy || selectedIds.length === 0} onClick={() => void confirm(false)} style={{ padding: "10px 13px", borderRadius: 9, fontWeight: 700 }}>
              <ShieldCheck size={16} style={{ verticalAlign: "middle", marginRight: 5 }} aria-hidden="true" /> Xác nhận, để bot tự chạy
            </button>
            <button type="button" disabled={busy || selectedIds.length === 0} onClick={() => void confirm(true)} style={{ padding: "10px 13px", borderRadius: 9 }}>
              Xác nhận + nhắc ngay Zalo
            </button>
          </div>
        </div>
      ) : status.excessSlotCount > 0 && status.enabled ? (
        <div style={{ marginTop: 14, padding: 12, borderRadius: 12, background: "rgba(34, 197, 94, .10)" }}>
          <CheckCircle2 size={18} style={{ verticalAlign: "middle", marginRight: 6 }} aria-hidden="true" />
          Automation đã có đủ state để tiếp tục. Ông chưa cần chỉnh thêm trên web.
        </div>
      ) : (
        <div style={{ marginTop: 14 }}>Roster hiện không còn exception dư slot cần xử lý.</div>
      )}
    </div>
  );
}

function StatusCard({ text, danger = false, onRetry }: { text: string; danger?: boolean; onRetry?: () => void }) {
  return (
    <div style={{ ...cardStyle(), marginTop: 12, borderColor: danger ? "rgba(248, 113, 113, .45)" : undefined }}>
      {text}
      {onRetry ? (
        <button type="button" onClick={onRetry} style={{ display: "block", marginTop: 10, padding: "8px 11px", borderRadius: 9 }}>
          Thử lại
        </button>
      ) : null}
    </div>
  );
}

export type { ExceptionFocus };
