import { useEffect, useMemo, useRef, useState } from "react";
import {
  BellRing,
  Check,
  ChevronDown,
  CircleDollarSign,
  Clock3,
  MessageSquareText,
  MoreHorizontal,
  Copy,
  KeyRound,
  QrCode,
  RefreshCw,
  Send,
  ShieldCheck,
  Users,
  X,
} from "lucide-react";
import {
  ApiRequestError,
  apiFetch,
  type MatchSettlementActionResponse,
  type MatchSettlementActionKind,
  type MatchSettlementActionRequest,
  type MatchSettlementAudience,
  type MatchSettlementEntryResponse,
  type MatchSettlementResponse,
  type MatchSettlementSlotFactor,
  type SessionResponse,
  type SettlementAccessChallengeResponse,
  type SettlementAccessChallengeStatusResponse,
  type SettlementAccessStatusResponse,
} from "../api/dbClient";

type ComposerAudience = "unpaid" | "paid" | "all" | "selected";
type DeliveryMode = "now" | "delay";
type DelayPreset = 30 | 60 | 120 | 360 | "custom";
type DelayUnit = "minutes" | "hours";
type SettlementAccessMode = "checking" | "admin" | "zalo" | "verify" | "denied";
type SettlementSessionSummary = Pick<SessionResponse, "name" | "startTime">;

const delayPresets: Array<{ label: string; minutes: Exclude<DelayPreset, "custom"> }> = [
  { label: "30 phút", minutes: 30 },
  { label: "1 giờ", minutes: 60 },
  { label: "2 giờ", minutes: 120 },
  { label: "6 giờ", minutes: 360 },
];

const quickPresets = [
  {
    id: "gentle",
    label: "Nhắc nhẹ",
    kind: "Reminder" as MatchSettlementActionKind,
    audience: "unpaid" as ComposerAudience,
    message: "Anh em chưa đóng tiền trận này chuyển giúp tui nha. Xong rồi thả ✅ để tui chốt sổ, cảm ơn mọi người 🙌",
  },
  {
    id: "thanks",
    label: "Cảm ơn",
    kind: "Thanks" as MatchSettlementActionKind,
    audience: "paid" as ComposerAudience,
    message: "Cảm ơn anh em đã chuyển khoản nhanh gọn nha 🙌 Hẹn mọi người kèo tiếp theo!",
  },
  {
    id: "summary",
    label: "Tổng kết",
    kind: "Summary" as MatchSettlementActionKind,
    audience: "all" as ComposerAudience,
    message: "Tổng kết tiền sân hôm nay đã cập nhật. Anh em xem lại giúp tui, ai còn thiếu thì chuyển khoản để mình chốt trận nha.",
  },
];

function formatMoney(value: number) {
  return new Intl.NumberFormat("vi-VN", { style: "currency", currency: "VND", maximumFractionDigits: 0 }).format(value);
}

function initials(name: string) {
  const parts = name.trim().split(/\s+/).filter(Boolean);
  return (parts.length > 1 ? `${parts[parts.length - 2]?.[0] ?? ""}${parts[parts.length - 1]?.[0] ?? ""}` : parts[0]?.slice(0, 2) ?? "?").toUpperCase();
}

function entryName(entry: MatchSettlementEntryResponse) {
  return entry.displayName.trim() || "Người chơi";
}

function backendAudience(audience: ComposerAudience): MatchSettlementAudience {
  switch (audience) {
    case "unpaid": return "Unpaid";
    case "paid": return "Paid";
    case "all": return "All";
    case "selected": return "Selected";
  }
}

function newClientRequestId() {
  return globalThis.crypto?.randomUUID?.() ?? `settlement-${Date.now()}-${Math.random().toString(36).slice(2)}`;
}

export function MatchSettlementFocus({ token, sessionId }: { token: string | null; sessionId: string }) {
  const [accessMode, setAccessMode] = useState<SettlementAccessMode>("checking");
  const [challenge, setChallenge] = useState<SettlementAccessChallengeResponse | null>(null);
  const [accessMessage, setAccessMessage] = useState<string | null>(null);
  const [session, setSession] = useState<SettlementSessionSummary | null>(null);
  const [settlement, setSettlement] = useState<MatchSettlementResponse | null>(null);
  const [missingSettlement, setMissingSettlement] = useState(false);
  const [amountInput, setAmountInput] = useState("");
  const [busy, setBusy] = useState(true);
  const [entryBusyId, setEntryBusyId] = useState<string | null>(null);
  const [savingAmount, setSavingAmount] = useState(false);
  const [error, setError] = useState<string | null>(null);
  const [composerOpen, setComposerOpen] = useState(false);
  const [actionKind, setActionKind] = useState<MatchSettlementActionKind>("Reminder");
  const [audience, setAudience] = useState<ComposerAudience>("unpaid");
  const [selectedIds, setSelectedIds] = useState<string[]>([]);
  const [message, setMessage] = useState(quickPresets[0].message);
  const [deliveryMode, setDeliveryMode] = useState<DeliveryMode>("now");
  const [delayPreset, setDelayPreset] = useState<DelayPreset>(30);
  const [customDelayValue, setCustomDelayValue] = useState(2);
  const [customDelayUnit, setCustomDelayUnit] = useState<DelayUnit>("hours");
  const [attachQr, setAttachQr] = useState(true);
  const [notice, setNotice] = useState<string | null>(null);
  const actionRequestIdRef = useRef(newClientRequestId());
  const pollingChallengeRef = useRef(false);

  const effectiveDelayMinutes = delayPreset === "custom"
    ? Math.min(43_200, Math.max(1, Math.round(customDelayValue * (customDelayUnit === "hours" ? 60 : 1))))
    : delayPreset;

  const delayLabel = delayPreset === "custom"
    ? `${customDelayValue} ${customDelayUnit === "hours" ? "giờ" : "phút"}`
    : delayPresets.find((option) => option.minutes === delayPreset)?.label ?? `${effectiveDelayMinutes} phút`;

  function changeCustomDelayUnit(nextUnit: DelayUnit) {
    const max = nextUnit === "hours" ? 720 : 43_200;
    setCustomDelayUnit(nextUnit);
    setCustomDelayValue((current) => Math.min(max, Math.max(1, current)));
  }

  function settlementPath(mode: "admin" | "zalo", suffix = "") {
    const encodedSessionId = encodeURIComponent(sessionId);
    const base = mode === "admin"
      ? `/sessions/${encodedSessionId}/settlement`
      : `/settlement-access/sessions/${encodedSessionId}/settlement`;
    return `${base}${suffix}`;
  }

  function tokenFor(mode: "admin" | "zalo") {
    return mode === "admin" ? token : null;
  }

  async function loadSettlement(showSpinner = true, mode: "admin" | "zalo" = accessMode === "admin" ? "admin" : "zalo") {
    if (showSpinner) setBusy(true);
    setError(null);
    try {
      if (mode === "admin") {
        const nextSession = await apiFetch<SessionResponse>(`/sessions/${encodeURIComponent(sessionId)}`, { token });
        setSession({ name: nextSession.name, startTime: nextSession.startTime });
      } else {
        const access = await apiFetch<SettlementAccessStatusResponse>(`/settlement-access/sessions/${encodeURIComponent(sessionId)}/status`);
        setSession({ name: access.sessionName, startTime: access.startTime });
      }
      try {
        const next = await apiFetch<MatchSettlementResponse>(settlementPath(mode), { token: tokenFor(mode) });
        setSettlement(next);
        setAmountInput(String(next.amountPerFullSlot || ""));
        setAttachQr(Boolean(next.paymentQrImageUrl));
        setMissingSettlement(false);
      } catch (caught) {
        if (caught instanceof ApiRequestError && caught.status === 404) {
          setSettlement(null);
          setMissingSettlement(true);
          setAmountInput("");
        } else {
          throw caught;
        }
      }
    } catch (caught) {
      setError(caught instanceof Error ? caught.message : "Không mở được dữ liệu chốt tiền trận này.");
    } finally {
      if (showSpinner) setBusy(false);
    }
  }

  async function beginChallenge(showSpinner = true) {
    if (showSpinner) setBusy(true);
    setError(null);
    setAccessMessage(null);
    try {
      const created = await apiFetch<SettlementAccessChallengeResponse>("/settlement-access/challenges", {
        method: "POST",
        body: { sessionId },
      });
      setChallenge(created);
      setSession({ name: created.sessionName, startTime: null });
      setAccessMode("verify");
      setAccessMessage("Gửi đúng câu lệnh bên dưới trong nhóm Zalo của trận. Trang này sẽ tự mở sau khi xác minh.");
    } catch (caught) {
      setAccessMode("denied");
      setError(caught instanceof Error ? caught.message : "Không tạo được mã xác minh Zalo.");
    } finally {
      if (showSpinner) setBusy(false);
    }
  }

  async function initializeAccess() {
    setBusy(true);
    setError(null);
    setAccessMessage(null);
    setChallenge(null);
    setSettlement(null);
    setSession(null);
    setAccessMode("checking");

    if (token) {
      try {
        await apiFetch<SessionResponse>(`/sessions/${encodeURIComponent(sessionId)}`, { token });
        setAccessMode("admin");
        await loadSettlement(false, "admin");
        setBusy(false);
        return;
      } catch {
        // A valid Draft admin session may still be outside the admin IP allowlist.
        // Fall through to the Zalo-scoped access path instead of showing a login form.
      }
    }

    try {
      const access = await apiFetch<SettlementAccessStatusResponse>(`/settlement-access/sessions/${encodeURIComponent(sessionId)}/status`);
      setSession({ name: access.sessionName, startTime: access.startTime });
      setAccessMode("zalo");
      await loadSettlement(false, "zalo");
    } catch (caught) {
      if (caught instanceof ApiRequestError && (caught.status === 401 || caught.status === 403)) {
        await beginChallenge(false);
      } else {
        setAccessMode("denied");
        setError(caught instanceof Error ? caught.message : "Không kiểm tra được quyền mở Sổ tiền.");
      }
    } finally {
      setBusy(false);
    }
  }

  useEffect(() => { void initializeAccess(); }, [sessionId, token]);

  useEffect(() => {
    if (accessMode !== "verify" || !challenge) return;
    let cancelled = false;

    const poll = async () => {
      if (cancelled || pollingChallengeRef.current) return;
      if (Date.now() >= new Date(challenge.expiresAt).getTime()) {
        setAccessMessage("Mã xác minh đã hết hạn. Tạo mã mới để tiếp tục.");
        return;
      }
      pollingChallengeRef.current = true;
      try {
        const status = await apiFetch<SettlementAccessChallengeStatusResponse>(
          "/settlement-access/challenges/redeem",
          {
            method: "POST",
            body: {
              challengeId: challenge.challengeId,
              redemptionSecret: challenge.redemptionSecret,
            },
          },
        );
        if (cancelled) return;
        setAccessMessage(status.message);
        if (status.status === "Approved" && status.authorized) {
          setAccessMode("zalo");
          setChallenge(null);
          await loadSettlement(true, "zalo");
        } else if (status.status === "Denied") {
          setAccessMode("denied");
        }
      } catch (caught) {
        if (!cancelled) {
          setAccessMessage(caught instanceof Error ? caught.message : "Chưa đọc được trạng thái xác minh.");
        }
      } finally {
        pollingChallengeRef.current = false;
      }
    };

    void poll();
    const timer = window.setInterval(() => { void poll(); }, 1500);
    return () => {
      cancelled = true;
      window.clearInterval(timer);
    };
  }, [accessMode, challenge?.challengeId, challenge?.expiresAt, sessionId]);

  const progress = useMemo(() => {
    if (!settlement) return 0;
    if (settlement.totalDue > 0) return Math.min(100, Math.round((settlement.totalCollected / settlement.totalDue) * 100));
    return settlement.totalCount > 0 ? Math.round((settlement.paidCount / settlement.totalCount) * 100) : 0;
  }, [settlement]);

  const sortedEntries = useMemo(() => {
    if (!settlement) return [];
    return [...settlement.entries].sort((left, right) => {
      if (left.isPaid !== right.isPaid) return left.isPaid ? 1 : -1;
      return entryName(left).localeCompare(entryName(right), "vi");
    });
  }, [settlement]);

  async function createSettlement() {
    const amount = Number(amountInput);
    if (!Number.isFinite(amount) || amount <= 0) return;
    setSavingAmount(true);
    setError(null);
    try {
      const mode = accessMode === "admin" ? "admin" : "zalo";
      const next = await apiFetch<MatchSettlementResponse>(settlementPath(mode), {
        method: "POST",
        token: tokenFor(mode),
        body: { amountPerFullSlot: amount },
      });
      setSettlement(next);
      setMissingSettlement(false);
      setAmountInput(String(next.amountPerFullSlot));
      setAttachQr(Boolean(next.paymentQrImageUrl));
      setNotice("Đã tạo sổ chốt tiền cho trận này.");
    } catch (caught) {
      setError(caught instanceof Error ? caught.message : "Không tạo được sổ chốt tiền.");
    } finally {
      setSavingAmount(false);
    }
  }

  async function saveAmount() {
    if (!settlement) return;
    const amount = Number(amountInput);
    if (!Number.isFinite(amount) || amount <= 0 || amount === settlement.amountPerFullSlot) return;
    setSavingAmount(true);
    setError(null);
    try {
      const mode = accessMode === "admin" ? "admin" : "zalo";
      const next = await apiFetch<MatchSettlementResponse>(settlementPath(mode), {
        method: "PUT",
        token: tokenFor(mode),
        body: { amountPerFullSlot: amount },
      });
      setSettlement(next);
      setAmountInput(String(next.amountPerFullSlot));
      setNotice("Đã cập nhật phí 1 slot.");
    } catch (caught) {
      setError(caught instanceof Error ? caught.message : "Không cập nhật được phí 1 slot.");
      setAmountInput(String(settlement.amountPerFullSlot));
    } finally {
      setSavingAmount(false);
    }
  }

  async function updateEntry(entry: MatchSettlementEntryResponse, patch: { slotFactor?: MatchSettlementSlotFactor; isPaid?: boolean }) {
    if (!settlement || entryBusyId) return;
    const slotFactor = patch.slotFactor ?? entry.slotFactor;
    const isPaid = patch.isPaid ?? entry.isPaid;
    setEntryBusyId(entry.id);
    setError(null);
    const nextEntries = settlement.entries.map((item) => item.id === entry.id
      ? { ...item, slotFactor, isPaid, amountDue: settlement.amountPerFullSlot * slotFactor }
      : item);
    setSettlement({
      ...settlement,
      entries: nextEntries,
      paidCount: nextEntries.filter((item) => item.isPaid).length,
      totalDue: nextEntries.reduce((total, item) => total + item.amountDue, 0),
      totalCollected: nextEntries.reduce((total, item) => total + (item.isPaid ? item.amountDue : 0), 0),
    });
    try {
      const mode = accessMode === "admin" ? "admin" : "zalo";
      await apiFetch(settlementPath(mode, `/entries/${encodeURIComponent(entry.id)}`), {
        method: "PUT",
        token: tokenFor(mode),
        body: { slotFactor, isPaid },
      });
      const refreshed = await apiFetch<MatchSettlementResponse>(settlementPath(mode), { token: tokenFor(mode) });
      setSettlement(refreshed);
      setAmountInput(String(refreshed.amountPerFullSlot));
    } catch (caught) {
      const updateError = caught instanceof Error ? caught.message : "Không cập nhật được người chơi.";
      await loadSettlement(false);
      setError((current) => current ?? updateError);
    } finally {
      setEntryBusyId(null);
    }
  }

  function openComposer(mode: "remind" | "compose" | "schedule") {
    setNotice(null);
    setError(null);
    actionRequestIdRef.current = newClientRequestId();
    if (mode === "remind") {
      const preset = quickPresets[0];
      setActionKind(preset.kind);
      setAudience(preset.audience);
      setMessage(preset.message);
      setDeliveryMode("now");
    } else if (mode === "compose") {
      setActionKind("Custom");
    } else {
      setDeliveryMode("delay");
    }
    setComposerOpen(true);
  }

  function applyPreset(preset: typeof quickPresets[number]) {
    setActionKind(preset.kind);
    setAudience(preset.audience);
    setMessage(preset.message);
  }

  function toggleSelected(id: string) {
    setSelectedIds((current) => current.includes(id) ? current.filter((item) => item !== id) : [...current, id]);
  }

  async function submitComposer() {
    if (!settlement || !message.trim()) return;
    const request: MatchSettlementActionRequest = {
      kind: actionKind,
      audience: backendAudience(audience),
      selectedEntryIds: audience === "selected" ? selectedIds : undefined,
      message: message.trim(),
      includePaymentQr: attachQr && Boolean(settlement.paymentQrImageUrl),
      delayMinutes: deliveryMode === "delay" ? effectiveDelayMinutes : undefined,
      clientRequestId: actionRequestIdRef.current,
    };
    setBusy(true);
    setError(null);
    try {
      const mode = accessMode === "admin" ? "admin" : "zalo";
      const action = await apiFetch<MatchSettlementActionResponse>(settlementPath(mode, "/actions"), {
        method: "POST",
        token: tokenFor(mode),
        body: request,
      });
      if (action.status === "Failed") {
        setError(action.lastError || "Tin settlement gửi thất bại.");
        return;
      } else if (action.status === "Skipped") {
        setNotice("Không có người phù hợp với nhóm nhận đã chọn nên bot không gửi tin.");
      } else if (action.status === "Sent") {
        setNotice("Đã gửi tin trên Zalo.");
      } else if (deliveryMode === "now") {
        setNotice("Bot chưa gửi được ngay; tin đã vào hàng đợi và sẽ tự thử lại.");
      } else {
        setNotice(`Đã hẹn gửi sau ${delayLabel}.`);
      }
      setComposerOpen(false);
    } catch (caught) {
      setError(caught instanceof Error ? caught.message : "Không gửi được tin settlement.");
    } finally {
      setBusy(false);
    }
  }

  if (accessMode === "checking") {
    return <div className="settlement-state">Đang kiểm tra quyền mở Sổ tiền…</div>;
  }

  if (accessMode === "verify" && challenge) {
    const expired = Date.now() >= new Date(challenge.expiresAt).getTime();
    return (
      <section className="settlement-access-shell">
        <div className="settlement-access-card">
          <div className="settlement-access-icon"><KeyRound size={26} aria-hidden="true" /></div>
          <span className="settlement-kicker">Xác minh Zalo một lần</span>
          <h2>Mở Sổ tiền không cần đăng nhập</h2>
          <p className="settlement-access-lead">
            Thiết bị này chưa được xác minh. Chỉ trưởng nhóm, phó nhóm hoặc operator được cấp quyền mới mở được màn thao tác.
          </p>
          <div className="settlement-access-session">
            <span>Trận</span>
            <strong>{challenge.sessionName}</strong>
          </div>
          <div className="settlement-access-code" aria-label={`Mã xác minh ${challenge.code}`}>
            {challenge.code}
          </div>
          <p>Trong đúng nhóm Zalo của trận, mention bot rồi gửi nội dung:</p>
          <div className="settlement-access-command">
            <code>{challenge.verificationCommand}</code>
            <button
              type="button"
              className="settlement-icon-button"
              aria-label="Sao chép câu lệnh xác minh"
              onClick={() => void navigator.clipboard?.writeText(challenge.verificationCommand)}
            >
              <Copy size={18} aria-hidden="true" />
            </button>
          </div>
          <div className="settlement-access-status" role="status">
            <ShieldCheck size={18} aria-hidden="true" />
            <span>{expired ? "Mã đã hết hạn." : accessMessage ?? "Đang chờ Zalo xác minh…"}</span>
          </div>
          <small>Mã có hiệu lực 10 phút. Sau khi xác minh thành công, thiết bị này được nhớ quyền trong tối đa 30 ngày và quyền Zalo vẫn được kiểm tra lại định kỳ.</small>
          {expired ? (
            <button type="button" className="settlement-submit" onClick={() => void beginChallenge()}>
              <RefreshCw size={18} aria-hidden="true" /> Tạo mã mới
            </button>
          ) : null}
        </div>
      </section>
    );
  }

  if (accessMode === "denied") {
    return (
      <section className="settlement-access-shell">
        <div className="settlement-access-card settlement-access-denied">
          <div className="settlement-access-icon"><ShieldCheck size={26} aria-hidden="true" /></div>
          <span className="settlement-kicker">Sổ tiền trận</span>
          <h2>Bạn chưa có quyền thao tác</h2>
          <p className="settlement-access-lead">
            Màn này chỉ dành cho trưởng nhóm, phó nhóm hoặc Zalo operator được admin cấp quyền. Thành viên thường không thể đánh dấu đã đóng, đổi slot hay gửi lời nhắc.
          </p>
          <div className="settlement-notice settlement-error-notice" role="alert">
            {error ?? accessMessage ?? "Tài khoản Zalo vừa xác minh không có quyền thao tác Sổ tiền."}
          </div>
          <button type="button" className="settlement-touch-button" onClick={() => void beginChallenge()}>
            <KeyRound size={18} aria-hidden="true" /> Xác minh tài khoản Zalo khác
          </button>
        </div>
      </section>
    );
  }

  if (busy && !session) {
    return <div className="settlement-state">Đang mở sổ chốt tiền…</div>;
  }

  if (error && !session) {
    return (
      <div className="settlement-state settlement-state-error">
        <strong>{error}</strong>
        <button type="button" className="settlement-touch-button" onClick={() => void loadSettlement()}>
          <RefreshCw size={18} aria-hidden="true" /> Thử lại
        </button>
      </div>
    );
  }

  if (!session) return null;

  if (missingSettlement || !settlement) {
    return (
      <section className="settlement-shell settlement-create-shell">
        <div className="settlement-hero">
          <div className="settlement-kicker"><CircleDollarSign size={16} aria-hidden="true" /> Chốt tiền sau trận</div>
          <h2>{session.name}</h2>
          <p>{session.startTime ? new Date(session.startTime).toLocaleString("vi-VN") : "Chưa có giờ thi đấu"}</p>
        </div>
        <div className="settlement-create-card">
          <div className="settlement-create-icon"><CircleDollarSign size={24} aria-hidden="true" /></div>
          <div>
            <span className="settlement-kicker">Sổ mới</span>
            <h3>Tạo sổ cho trận này</h3>
            <p>Nhập phí của 1 slot. Người đang tham gia trận sẽ được đưa vào sổ và mặc định là chưa đóng.</p>
          </div>
          <label className="settlement-create-amount">
            <span>Phí 1 slot</span>
            <div><input autoFocus inputMode="numeric" value={amountInput} onChange={(event) => setAmountInput(event.target.value.replace(/\D/g, ""))} placeholder="Ví dụ 70000" /><span>đ</span></div>
          </label>
          {error ? <div className="settlement-notice settlement-error-notice" role="alert">{error}</div> : null}
          <button className="settlement-submit" type="button" disabled={savingAmount || Number(amountInput) <= 0} onClick={() => void createSettlement()}>
            <CircleDollarSign size={18} aria-hidden="true" /> {savingAmount ? "Đang tạo…" : "Tạo sổ chốt tiền"}
          </button>
        </div>
      </section>
    );
  }

  const unpaidCount = Math.max(0, settlement.totalCount - settlement.paidCount);

  return (
    <section className="settlement-shell">
      <div className="settlement-hero">
        <div className="settlement-hero-row">
          <div>
            <div className="settlement-kicker"><CircleDollarSign size={16} aria-hidden="true" /> Chốt tiền sau trận</div>
            <h2>{settlement.sessionName}</h2>
            <p>{session.startTime ? new Date(session.startTime).toLocaleString("vi-VN", { weekday: "short", day: "2-digit", month: "2-digit", hour: "2-digit", minute: "2-digit" }) : "Chưa có giờ thi đấu"}</p>
          </div>
          <button className="settlement-icon-button" type="button" aria-label="Làm mới" onClick={() => void loadSettlement()} disabled={busy}>
            <RefreshCw size={19} aria-hidden="true" />
          </button>
        </div>

        <div className="settlement-progress-copy">
          <strong>{progress}% đã thu</strong>
          <span>{settlement.paidCount}/{settlement.totalCount} người</span>
        </div>
        <div className="settlement-progress" aria-label={`${progress}% đã thu`}><span style={{ width: `${progress}%` }} /></div>

        <div className="settlement-money-grid">
          <div><span>Cần thu</span><strong>{formatMoney(settlement.totalDue)}</strong></div>
          <div><span>Đã thu</span><strong>{formatMoney(settlement.totalCollected)}</strong></div>
        </div>

        <label className="settlement-fee-field">
          <span>Phí 1 slot</span>
          <div>
            <input inputMode="numeric" value={amountInput} onChange={(event) => setAmountInput(event.target.value.replace(/\D/g, ""))} onKeyDown={(event) => { if (event.key === "Enter") void saveAmount(); }} />
            <span>đ</span>
          </div>
          <button type="button" disabled={savingAmount || Number(amountInput) <= 0 || Number(amountInput) === settlement.amountPerFullSlot} onClick={() => void saveAmount()}>
            {savingAmount ? "Đang lưu…" : "Lưu"}
          </button>
        </label>
      </div>

      {error && !composerOpen ? <div className="settlement-notice settlement-error-notice" role="alert">{error}</div> : null}
      {notice ? <div className="settlement-notice" role="status">{notice}</div> : null}

      <div className="settlement-section-heading">
        <div><span>Danh sách người chơi</span><strong>{unpaidCount} chưa đóng</strong></div>
      </div>

      <div className="settlement-player-list">
        {sortedEntries.map((entry) => {
          const name = entryName(entry);
          const amount = entry.amountDue;
          const isUpdating = entryBusyId === entry.id;
          return (
            <article className={`settlement-player-card ${entry.isPaid ? "is-paid" : "is-unpaid"}`} key={entry.id} aria-busy={isUpdating}>
              <div className="settlement-player-main">
                <div className="settlement-avatar" aria-hidden="true">{initials(name)}</div>
                <div className="settlement-player-copy"><strong>{name}</strong><span>{formatMoney(amount)}</span></div>
                <span className={`settlement-status-pill ${entry.isPaid ? "paid" : "unpaid"}`}>
                  {isUpdating ? "Đang lưu…" : entry.isPaid ? "Đã đóng" : "Chưa đóng"}
                </span>
              </div>

              <div className="settlement-card-controls">
                <div className="settlement-segment" aria-label={`Trạng thái thanh toán của ${name}`}>
                  <button aria-pressed={!entry.isPaid} disabled={isUpdating} type="button" className={!entry.isPaid ? "active" : ""} onClick={() => void updateEntry(entry, { isPaid: false })}>Chưa đóng</button>
                  <button aria-pressed={entry.isPaid} disabled={isUpdating} type="button" className={entry.isPaid ? "active paid" : ""} onClick={() => void updateEntry(entry, { isPaid: true })}><Check size={16} aria-hidden="true" /> Đã đóng</button>
                </div>
                <div className="settlement-slot-control" aria-label={`Số slot của ${name}`}>
                  <button aria-pressed={entry.slotFactor === 1} disabled={isUpdating} type="button" className={entry.slotFactor === 1 ? "active" : ""} onClick={() => void updateEntry(entry, { slotFactor: 1 })}>1 slot</button>
                  <button aria-pressed={entry.slotFactor === 0.5} disabled={isUpdating} type="button" className={entry.slotFactor === 0.5 ? "active" : ""} onClick={() => void updateEntry(entry, { slotFactor: 0.5 })}>1/2 slot</button>
                </div>
              </div>
            </article>
          );
        })}
      </div>

      {settlement.entries.length === 0 ? <div className="settlement-empty">Sổ chưa có người chơi.</div> : null}

      <div className="settlement-sticky-actions" aria-label="Thao tác chốt tiền">
        <button type="button" className="primary" onClick={() => {
          if (unpaidCount > 0) {
            openComposer("remind");
          } else {
            actionRequestIdRef.current = newClientRequestId();
            applyPreset(quickPresets[1]);
            setDeliveryMode("now");
            setComposerOpen(true);
          }
        }}>
          {unpaidCount > 0 ? <BellRing size={19} aria-hidden="true" /> : <Send size={19} aria-hidden="true" />}
          <span>{unpaidCount > 0 ? "Réo chưa đóng" : "Cảm ơn"}</span>
        </button>
        <button type="button" onClick={() => openComposer("compose")}><MessageSquareText size={19} aria-hidden="true" /><span>Soạn tin</span></button>
        <button type="button" onClick={() => openComposer("schedule")}><MoreHorizontal size={20} aria-hidden="true" /><span>Hẹn giờ</span></button>
      </div>

      {composerOpen ? (
        <div className="settlement-sheet-backdrop" role="presentation" onMouseDown={(event) => { if (event.currentTarget === event.target) setComposerOpen(false); }}>
          <section className="settlement-sheet" role="dialog" aria-modal="true" aria-labelledby="settlement-composer-title">
            <div className="settlement-sheet-handle" />
            <div className="settlement-sheet-heading">
              <div><span className="settlement-kicker"><MessageSquareText size={15} aria-hidden="true" /> Tin nhắn Zalo</span><h3 id="settlement-composer-title">Soạn tin nhắn</h3></div>
              <button className="settlement-icon-button" type="button" aria-label="Đóng" onClick={() => setComposerOpen(false)}><X size={20} /></button>
            </div>

            {error ? <div className="settlement-notice settlement-error-notice" role="alert">{error}</div> : null}

            <div className="settlement-presets" aria-label="Mẫu tin nhanh">
              {quickPresets.map((preset) => {
                const isActive = actionKind === preset.kind && audience === preset.audience && message === preset.message;
                return <button type="button" aria-pressed={isActive} className={isActive ? "active" : ""} key={preset.id} onClick={() => applyPreset(preset)}>{preset.label}</button>;
              })}
            </div>

            <label className="settlement-composer-field">
              <span>Gửi cho</span>
              <div className="settlement-select-wrap">
                <Users size={18} aria-hidden="true" />
                <select value={audience} onChange={(event) => setAudience(event.target.value as ComposerAudience)}>
                  <option value="unpaid">Chưa đóng ({unpaidCount})</option>
                  <option value="paid">Đã đóng ({settlement.paidCount})</option>
                  <option value="all">Tất cả ({settlement.totalCount})</option>
                  <option value="selected">Tự chọn ({selectedIds.length})</option>
                </select>
                <ChevronDown size={17} aria-hidden="true" />
              </div>
            </label>

            {audience === "selected" ? (
              <div className="settlement-selected-list">
                {sortedEntries.map((entry) => (
                  <label key={entry.id}><input type="checkbox" checked={selectedIds.includes(entry.id)} onChange={() => toggleSelected(entry.id)} /><span>{entryName(entry)}</span></label>
                ))}
              </div>
            ) : null}

            <label className="settlement-composer-field">
              <span>Nội dung</span>
              <textarea rows={5} value={message} onChange={(event) => { setActionKind("Custom"); setMessage(event.target.value); }} placeholder="Nhập lời nhắn…" />
            </label>

            <div className="settlement-composer-options">
              <button type="button" aria-pressed={attachQr} className={attachQr ? "active" : ""} onClick={() => setAttachQr((value) => !value)} disabled={!settlement.paymentQrImageUrl}>
                <QrCode size={18} aria-hidden="true" /> QR chuyển khoản
                {attachQr && settlement.paymentQrImageUrl ? <Check size={16} aria-hidden="true" /> : null}
              </button>
            </div>

            <div className="settlement-delivery-grid">
              <button type="button" aria-pressed={deliveryMode === "now"} className={deliveryMode === "now" ? "active" : ""} onClick={() => setDeliveryMode("now")}><Send size={18} aria-hidden="true" /><span><strong>Gửi ngay</strong><small>Gửi khi bấm nút bên dưới</small></span></button>
              <button type="button" aria-pressed={deliveryMode === "delay"} className={deliveryMode === "delay" ? "active" : ""} onClick={() => setDeliveryMode("delay")}><Clock3 size={18} aria-hidden="true" /><span><strong>Hẹn giờ</strong><small>Chọn thời gian bên dưới</small></span></button>
            </div>

            {deliveryMode === "delay" ? (
              <div className="settlement-delay-panel">
                <span className="settlement-delay-label">Gửi sau</span>
                <div className="settlement-delay-presets" aria-label="Chọn thời gian hẹn gửi">
                  {delayPresets.map((option) => (
                    <button
                      type="button"
                      key={option.minutes}
                      aria-pressed={delayPreset === option.minutes}
                      className={delayPreset === option.minutes ? "active" : ""}
                      onClick={() => setDelayPreset(option.minutes)}
                    >
                      {option.label}
                    </button>
                  ))}
                  <button
                    type="button"
                    aria-pressed={delayPreset === "custom"}
                    className={delayPreset === "custom" ? "active" : ""}
                    onClick={() => setDelayPreset("custom")}
                  >
                    Tự chọn
                  </button>
                </div>
                {delayPreset === "custom" ? (
                  <div className="settlement-delay-custom">
                    <input
                      type="number"
                      min={1}
                      max={customDelayUnit === "hours" ? 720 : 43_200}
                      inputMode="numeric"
                      value={customDelayValue}
                      onChange={(event) => {
                        const max = customDelayUnit === "hours" ? 720 : 43_200;
                        setCustomDelayValue(Math.min(max, Math.max(1, Number(event.target.value) || 1)));
                      }}
                      aria-label="Khoảng thời gian hẹn gửi"
                    />
                    <select
                      value={customDelayUnit}
                      onChange={(event) => changeCustomDelayUnit(event.target.value as DelayUnit)}
                      aria-label="Đơn vị thời gian"
                    >
                      <option value="minutes">phút</option>
                      <option value="hours">giờ</option>
                    </select>
                  </div>
                ) : null}
                <div className="settlement-delay-summary">Bot sẽ gửi sau {delayLabel}.</div>
              </div>
            ) : null}

            <button className="settlement-submit" type="button" disabled={busy || !message.trim() || (audience === "selected" && selectedIds.length === 0)} onClick={() => void submitComposer()}>
              {deliveryMode === "now" ? <Send size={18} aria-hidden="true" /> : <Clock3 size={18} aria-hidden="true" />}
              {busy ? "Đang gửi…" : deliveryMode === "now" ? "Gửi ngay" : `Hẹn sau ${delayLabel}`}
            </button>
          </section>
        </div>
      ) : null}
    </section>
  );
}
