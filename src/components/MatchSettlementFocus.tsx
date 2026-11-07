import { useEffect, useMemo, useRef, useState } from "react";
import {
  BellRing,
  Check,
  ChevronDown,
  CircleDollarSign,
  Clock3,
  MessageSquareText,
  Copy,
  Download,
  KeyRound,
  QrCode,
  RefreshCw,
  Send,
  ShieldCheck,
  Users,
  Volleyball,
  X,
} from "lucide-react";
import {
  ApiRequestError,
  apiDownload,
  apiFetch,
  apiUpload,
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
    message: "",
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

function ZyroBrand({ compact = false }: { compact?: boolean }) {
  return (
    <div className={`settlement-zyro-brand ${compact ? "compact" : ""}`} aria-label="ZYRO Volleyball Club">
      <span className="settlement-zyro-mark" aria-hidden="true"><Volleyball size={compact ? 20 : 24} /></span>
      <span className="settlement-zyro-wordmark"><strong>ZYRO</strong><small>VOLLEYBALL CLUB</small></span>
    </div>
  );
}

export function MatchSettlementFocus({ token, sessionId }: { token: string | null; sessionId: string }) {
  const [accessMode, setAccessMode] = useState<SettlementAccessMode>("checking");
  const [challenge, setChallenge] = useState<SettlementAccessChallengeResponse | null>(null);
  const [accessMessage, setAccessMessage] = useState<string | null>(null);
  const [session, setSession] = useState<SettlementSessionSummary | null>(null);
  const [settlement, setSettlement] = useState<MatchSettlementResponse | null>(null);
  const [missingSettlement, setMissingSettlement] = useState(false);
  const [amountInput, setAmountInput] = useState("");
  const [totalDueInput, setTotalDueInput] = useState("");
  const [receivedInputs, setReceivedInputs] = useState<Record<string, string>>({});
  const [busy, setBusy] = useState(true);
  const [entryBusyId, setEntryBusyId] = useState<string | null>(null);
  const [savingAmount, setSavingAmount] = useState(false);
  const [savingTotalDue, setSavingTotalDue] = useState(false);
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
  const [qrUploading, setQrUploading] = useState(false);
  const [qrSending, setQrSending] = useState(false);
  const [exportingExcel, setExportingExcel] = useState(false);
  const [notice, setNotice] = useState<string | null>(null);
  const actionRequestIdRef = useRef(newClientRequestId());
  const pollingChallengeRef = useRef(false);
  const composerDialogRef = useRef<HTMLElement | null>(null);
  const composerReturnFocusRef = useRef<HTMLElement | null>(null);
  const qrFileInputRef = useRef<HTMLInputElement | null>(null);

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

  function applySettlementSnapshot(next: MatchSettlementResponse) {
    setSettlement(next);
    setAmountInput(String(next.amountPerFullSlot || ""));
    setTotalDueInput(String(next.totalDue || ""));
    setReceivedInputs(Object.fromEntries(next.entries.map((entry) => [entry.id, String(entry.actualPaidAmount || entry.amountDue || "")])));
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
        applySettlementSnapshot(next);
        setAttachQr(Boolean(next.paymentQrImageUrl));
        setMissingSettlement(false);
      } catch (caught) {
        if (caught instanceof ApiRequestError && caught.status === 404) {
          setSettlement(null);
          setReceivedInputs({});
          setMissingSettlement(true);
          setAmountInput("");
          setTotalDueInput("");
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

  async function exportExcel() {
    const mode: "admin" | "zalo" = accessMode === "admin" ? "admin" : "zalo";
    setExportingExcel(true);
    setError(null);
    setNotice(null);
    try {
      const { blob, fileName } = await apiDownload(settlementPath(mode, "/export"), tokenFor(mode));
      const url = URL.createObjectURL(blob);
      const anchor = document.createElement("a");
      anchor.href = url;
      anchor.download = fileName;
      document.body.appendChild(anchor);
      anchor.click();
      anchor.remove();
      URL.revokeObjectURL(url);
      setNotice("Đã xuất Excel đối soát. File có Tổng quan, Thu tiền, Sổ quỹ và Nhật ký Zalo.");
    } catch (caught) {
      setError(caught instanceof Error ? caught.message : "Không xuất được Excel. Bạn thử lại nhé.");
    } finally {
      setExportingExcel(false);
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
      applySettlementSnapshot(next);
      setMissingSettlement(false);
      setAttachQr(Boolean(next.paymentQrImageUrl));
      setNotice("Đã tạo sổ chốt tiền cho trận này.");
    } catch (caught) {
      setError(caught instanceof Error ? caught.message : "Không tạo được sổ chốt tiền.");
    } finally {
      setSavingAmount(false);
    }
  }

  async function saveTotalDue() {
    if (!settlement) return;
    const totalDue = Number(totalDueInput);
    if (!Number.isFinite(totalDue) || totalDue <= 0 || totalDue === settlement.totalDue) return;
    setSavingTotalDue(true);
    setError(null);
    try {
      const mode = accessMode === "admin" ? "admin" : "zalo";
      const next = await apiFetch<MatchSettlementResponse>(settlementPath(mode), {
        method: "PUT",
        token: tokenFor(mode),
        body: {
          amountPerFullSlot: settlement.amountPerFullSlot,
          totalDueOverride: totalDue,
        },
      });
      applySettlementSnapshot(next);
      setNotice("Đã cập nhật TỔNG CẦN THU của trận. Tiền thực nhận từng người vẫn giữ nguyên và quỹ đã được đối soát lại.");
    } catch (caught) {
      setError(caught instanceof Error ? caught.message : "Không cập nhật được tổng cần thu.");
      setTotalDueInput(String(settlement.totalDue));
    } finally {
      setSavingTotalDue(false);
    }
  }

  async function updateEntry(entry: MatchSettlementEntryResponse, patch: { slotFactor?: MatchSettlementSlotFactor; isPaid?: boolean; actualPaidAmount?: number }) {
    if (!settlement || entryBusyId) return;
    const slotFactor = patch.slotFactor ?? entry.slotFactor;
    const isPaid = patch.isPaid ?? entry.isPaid;
    const amountDue = settlement.amountPerFullSlot * slotFactor;
    let actualPaidAmount = 0;
    if (isPaid) {
      if (patch.actualPaidAmount !== undefined) {
        actualPaidAmount = patch.actualPaidAmount;
      } else if (!entry.isPaid) {
        actualPaidAmount = amountDue;
      } else {
        actualPaidAmount = entry.actualPaidAmount;
      }
      if (!Number.isFinite(actualPaidAmount) || actualPaidAmount < amountDue) {
        setError(`Tiền thực nhận phải ít nhất ${formatMoney(amountDue)}. Sổ không dùng trạng thái đóng một phần.`);
        return;
      }
    }
    setEntryBusyId(entry.id);
    setError(null);
    const nextEntries = settlement.entries.map((item) => item.id === entry.id
      ? { ...item, slotFactor, isPaid, paymentStatus: isPaid ? "Paid" as const : "Unpaid" as const, amountDue, actualPaidAmount, surplusAmount: isPaid ? Math.max(0, actualPaidAmount - amountDue) : 0 }
      : item);
    const derivedTotalDue = nextEntries.reduce((total, item) => total + item.amountDue, 0);
    const effectiveTotalDue = settlement.totalDueOverride ?? derivedTotalDue;
    const entrySurplus = nextEntries.reduce((total, item) => total + item.surplusAmount, 0);
    const nextTotalReceived = nextEntries.reduce((total, item) => total + (item.isPaid ? item.actualPaidAmount : 0), 0);
    const nextSettlementSurplus = settlement.totalDueOverride !== null
      ? Math.max(0, nextTotalReceived - effectiveTotalDue)
      : entrySurplus;
    setSettlement({
      ...settlement,
      entries: nextEntries,
      paidCount: nextEntries.filter((item) => item.isPaid).length,
      totalDue: effectiveTotalDue,
      totalCollected: nextEntries.reduce((total, item) => total + (item.isPaid ? item.amountDue : 0), 0),
      totalReceived: nextTotalReceived,
      settlementSurplus: nextSettlementSurplus,
      groupFundBalance: settlement.groupFundBalance + (nextSettlementSurplus - settlement.settlementSurplus),
    });
    setReceivedInputs((current) => ({ ...current, [entry.id]: isPaid ? String(actualPaidAmount) : "" }));
    try {
      const mode = accessMode === "admin" ? "admin" : "zalo";
      await apiFetch(settlementPath(mode, `/entries/${encodeURIComponent(entry.id)}`), {
        method: "PUT",
        token: tokenFor(mode),
        body: { slotFactor, isPaid, actualPaidAmount },
      });
      const refreshed = await apiFetch<MatchSettlementResponse>(settlementPath(mode), { token: tokenFor(mode) });
      applySettlementSnapshot(refreshed);
    } catch (caught) {
      const updateError = caught instanceof Error ? caught.message : "Không cập nhật được người chơi.";
      await loadSettlement(false);
      setError((current) => current ?? updateError);
    } finally {
      setEntryBusyId(null);
    }
  }

  async function saveReceivedAmount(entry: MatchSettlementEntryResponse) {
    if (!settlement || !entry.isPaid) return;
    const value = Number(receivedInputs[entry.id]);
    if (!Number.isFinite(value) || value < entry.amountDue) {
      setError(`Tiền thực nhận của ${entryName(entry)} phải ít nhất ${formatMoney(entry.amountDue)}.`);
      return;
    }
    if (value === entry.actualPaidAmount) return;
    await updateEntry(entry, { actualPaidAmount: value });
  }

  async function uploadPaymentQr(file: File) {
    if (!settlement) return;
    const allowedTypes = new Set(["image/jpeg", "image/jpg", "image/png", "image/webp"]);
    if (!allowedTypes.has(file.type)) {
      setError("Chỉ hỗ trợ ảnh QR dạng JPG, PNG hoặc WEBP.");
      return;
    }
    if (file.size > 10 * 1024 * 1024) {
      setError("Ảnh QR không được lớn hơn 10 MB.");
      return;
    }

    setQrUploading(true);
    setError(null);
    setNotice(null);
    try {
      const mode = accessMode === "admin" ? "admin" : "zalo";
      const next = await apiUpload<MatchSettlementResponse>(
        settlementPath(mode, "/payment-qr"),
        file,
        tokenFor(mode),
      );
      applySettlementSnapshot(next);
      setAttachQr(true);
      setNotice("Đã lưu QR cho trận này. Bạn có thể đính kèm vào tin hoặc gửi ngay lên nhóm Zalo.");
    } catch (caught) {
      setError(caught instanceof Error ? caught.message : "Không tải được ảnh QR.");
    } finally {
      setQrUploading(false);
      if (qrFileInputRef.current) qrFileInputRef.current.value = "";
    }
  }

  async function sendPaymentQrNow() {
    if (!settlement?.paymentQrImageUrl || qrSending) return;
    setQrSending(true);
    setError(null);
    setNotice(null);
    try {
      const mode = accessMode === "admin" ? "admin" : "zalo";
      const action = await apiFetch<MatchSettlementActionResponse>(settlementPath(mode, "/actions"), {
        method: "POST",
        token: tokenFor(mode),
        body: {
          kind: "PaymentQr",
          audience: "All",
          includePaymentQr: true,
          clientRequestId: newClientRequestId(),
        } satisfies MatchSettlementActionRequest,
      });
      if (action.status === "Failed") {
        setError(action.lastError || "Gửi QR lên Zalo thất bại.");
      } else if (action.status === "Sent") {
        setNotice("Đã gửi QR thanh toán lên nhóm Zalo, không tag @all.");
      } else {
        setNotice("QR đã vào hàng đợi gửi và bot sẽ tự thử lại nếu cần.");
      }
    } catch (caught) {
      setError(caught instanceof Error ? caught.message : "Không gửi được QR lên Zalo.");
    } finally {
      setQrSending(false);
    }
  }

  function openComposer(mode: "remind" | "compose" | "schedule" | "summary") {
    setNotice(null);
    setError(null);
    actionRequestIdRef.current = newClientRequestId();
    composerReturnFocusRef.current = document.activeElement instanceof HTMLElement ? document.activeElement : null;
    setSelectedIds([]);
    setDelayPreset(30);
    if (mode === "remind") {
      const preset = quickPresets[0];
      setActionKind(preset.kind);
      setAudience(preset.audience);
      setMessage(preset.message);
      setDeliveryMode("now");
      setAttachQr(Boolean(settlement?.paymentQrImageUrl));
    } else if (mode === "compose") {
      setActionKind("Custom");
      setAudience("unpaid");
      setMessage("");
      setDeliveryMode("now");
      setAttachQr(false);
    } else if (mode === "schedule") {
      const preset = quickPresets[0];
      setActionKind(preset.kind);
      setAudience(preset.audience);
      setMessage(preset.message);
      setDeliveryMode("delay");
      setAttachQr(Boolean(settlement?.paymentQrImageUrl));
    } else {
      const preset = quickPresets[2];
      setActionKind(preset.kind);
      setAudience(preset.audience);
      setMessage(preset.message);
      setDeliveryMode("now");
      setAttachQr(false);
    }
    setComposerOpen(true);
  }

  function closeComposer() {
    setComposerOpen(false);
    window.setTimeout(() => composerReturnFocusRef.current?.focus(), 0);
  }

  useEffect(() => {
    if (!composerOpen) return;
    const dialog = composerDialogRef.current;
    if (!dialog) return;

    const focusables = () => Array.from(dialog.querySelectorAll<HTMLElement>(
      'button:not([disabled]), select:not([disabled]), textarea:not([disabled]), input:not([disabled]), [tabindex]:not([tabindex="-1"])',
    )).filter((element) => !element.hasAttribute("hidden"));
    window.requestAnimationFrame(() => focusables()[0]?.focus());

    const handleKeyDown = (event: KeyboardEvent) => {
      if (event.key === "Escape") {
        event.preventDefault();
        closeComposer();
        return;
      }
      if (event.key !== "Tab") return;
      const items = focusables();
      if (items.length === 0) return;
      const first = items[0];
      const last = items[items.length - 1];
      if (event.shiftKey && document.activeElement === first) {
        event.preventDefault();
        last.focus();
      } else if (!event.shiftKey && document.activeElement === last) {
        event.preventDefault();
        first.focus();
      }
    };
    document.addEventListener("keydown", handleKeyDown);
    return () => document.removeEventListener("keydown", handleKeyDown);
  }, [composerOpen]);

  function applyPreset(preset: typeof quickPresets[number]) {
    setActionKind(preset.kind);
    setAudience(preset.audience);
    setMessage(preset.message);
    setAttachQr(preset.kind === "Summary" ? false : Boolean(settlement?.paymentQrImageUrl));
  }

  function toggleSelected(id: string) {
    setSelectedIds((current) => current.includes(id) ? current.filter((item) => item !== id) : [...current, id]);
  }

  async function submitComposer() {
    if (!settlement || (actionKind !== "Summary" && !message.trim())) return;
    const isSummary = actionKind === "Summary";
    const request: MatchSettlementActionRequest = {
      kind: actionKind,
      audience: isSummary ? "All" : backendAudience(audience),
      selectedEntryIds: !isSummary && audience === "selected" ? selectedIds : undefined,
      message: message.trim() || undefined,
      includePaymentQr: !isSummary && attachQr && Boolean(settlement.paymentQrImageUrl),
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
        setNotice(isSummary ? "Đã gửi ảnh tổng kết lên nhóm Zalo, không tag @all." : "Đã gửi tin trên Zalo.");
      } else if (deliveryMode === "now") {
        setNotice("Bot chưa gửi được ngay; tin đã vào hàng đợi và sẽ tự thử lại.");
      } else {
        setNotice(`Đã hẹn gửi sau ${delayLabel}.`);
      }
      closeComposer();
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
          <ZyroBrand />
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
          <ZyroBrand />
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
          <ZyroBrand />
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
        <div className="settlement-brand-row">
          <ZyroBrand />
          <span className="settlement-brand-tag">SỔ TIỀN TRẬN</span>
        </div>
        <div className="settlement-hero-row">
          <div>
            <div className="settlement-kicker"><CircleDollarSign size={16} aria-hidden="true" /> Chốt tiền sau trận</div>
            <h2>{settlement.sessionName}</h2>
            <p>{session.startTime ? new Date(session.startTime).toLocaleString("vi-VN", { weekday: "short", day: "2-digit", month: "2-digit", hour: "2-digit", minute: "2-digit" }) : "Chưa có giờ thi đấu"}</p>
          </div>
          <div className="settlement-hero-actions">
            <button className="settlement-export-button" type="button" onClick={() => void exportExcel()} disabled={busy || exportingExcel}>
              <Download size={18} aria-hidden="true" /> {exportingExcel ? "Đang xuất…" : "Xuất Excel"}
            </button>
            <button className="settlement-icon-button" type="button" aria-label="Làm mới" onClick={() => void loadSettlement()} disabled={busy}>
              <RefreshCw size={19} aria-hidden="true" />
            </button>
          </div>
        </div>

        <div className="settlement-progress-copy">
          <strong>{progress}% đã thu</strong>
          <span>{settlement.paidCount}/{settlement.totalCount} người</span>
        </div>
        <div className="settlement-progress" aria-label={`${progress}% đã thu`}><span style={{ width: `${progress}%` }} /></div>

        <div className="settlement-fund-highlight">
          <div><span>QUỸ ZYRO</span><strong>{formatMoney(settlement.groupFundBalance)}</strong></div>
          <small>{settlement.settlementSurplus > 0 ? `+${formatMoney(settlement.settlementSurplus)} từ trận này` : "Trận này chưa phát sinh tiền dư"}</small>
        </div>

        <div className="settlement-money-grid">
          <div><span>Cần thu</span><strong>{formatMoney(settlement.totalDue)}</strong></div>
          <div><span>Đã nhận</span><strong>{formatMoney(settlement.totalReceived)}</strong></div>
          <div className="fund"><span>Dư trận này</span><strong>{settlement.settlementSurplus > 0 ? "+" : ""}{formatMoney(settlement.settlementSurplus)}</strong></div>
        </div>

        <section className="settlement-fee-card" aria-label="Tổng cần thu của trận">
          <div className="settlement-fee-card-copy">
            <span className="settlement-fee-card-kicker">TỔNG CẦN THU CỦA TRẬN</span>
            <strong>{formatMoney(settlement.totalDue)}</strong>
            <small>
              Mức theo người: 1 slot {formatMoney(settlement.amountPerFullSlot)} • 1/2 slot {formatMoney(settlement.amountPerFullSlot / 2)}.
              {settlement.totalDueOverride !== null ? " Tổng này đã được chỉnh trực tiếp." : " Tổng này hiện được tính tự động từ mức theo người."}
              {" "}Sửa tổng không ghi đè tiền thực nhận từng người; hệ thống tự đối soát lại phần dư/quỹ.
            </small>
          </div>
          <label className="settlement-fee-editor">
            <span>Nhập tổng mới</span>
            <div>
              <input
                inputMode="numeric"
                aria-label="Tổng cần thu mới của trận"
                value={totalDueInput}
                onChange={(event) => setTotalDueInput(event.target.value.replace(/\D/g, ""))}
                onKeyDown={(event) => { if (event.key === "Enter") void saveTotalDue(); }}
              />
              <span>đ</span>
            </div>
          </label>
          <button
            className="settlement-fee-save"
            type="button"
            disabled={savingTotalDue || Number(totalDueInput) <= 0 || Number(totalDueInput) === settlement.totalDue}
            onClick={() => void saveTotalDue()}
          >
            {savingTotalDue ? "Đang cập nhật…" : "Cập nhật tổng cần thu"}
          </button>
        </section>
      </div>

      {error && !composerOpen ? <div className="settlement-notice settlement-error-notice" role="alert">{error}</div> : null}
      {notice && !composerOpen ? <div className="settlement-notice" role="status">{notice}</div> : null}

      <input
        ref={qrFileInputRef}
        className="settlement-hidden-file"
        type="file"
        accept="image/jpeg,image/png,image/webp"
        onChange={(event) => {
          const file = event.target.files?.[0];
          if (file) void uploadPaymentQr(file);
        }}
      />

      <section className="settlement-qr-card" aria-label="QR thanh toán">
        <div className="settlement-qr-preview">
          {settlement.paymentQrImageUrl
            ? <img src={settlement.paymentQrImageUrl} alt="QR thanh toán của trận" />
            : <QrCode size={34} aria-hidden="true" />}
        </div>
        <div className="settlement-qr-copy">
          <span>QR thanh toán</span>
          <strong>{settlement.paymentQrImageUrl ? "Đã sẵn sàng" : "Chưa có QR"}</strong>
          <small>{settlement.paymentQrImageUrl ? "Dùng lại cho tin nhắc hoặc gửi riêng ngay lên nhóm." : "Chọn ảnh ngay tại đây, không cần vào cài đặt bot."}</small>
        </div>
        <div className="settlement-qr-actions">
          <button type="button" disabled={qrUploading || qrSending} onClick={() => qrFileInputRef.current?.click()}>
            <QrCode size={17} aria-hidden="true" /> {qrUploading ? "Đang tải…" : settlement.paymentQrImageUrl ? "Đổi QR" : "Thêm QR"}
          </button>
          <button className="primary" type="button" disabled={!settlement.paymentQrImageUrl || qrUploading || qrSending} onClick={() => void sendPaymentQrNow()}>
            <Send size={17} aria-hidden="true" /> {qrSending ? "Đang gửi…" : "Gửi QR ngay"}
          </button>
        </div>
      </section>

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

              {entry.isPaid ? (
                <div className="settlement-received-row">
                  <div className="settlement-received-copy">
                    <span>Thực nhận</span>
                    <small>{entry.surplusAmount > 0 ? `Dư vào quỹ +${formatMoney(entry.surplusAmount)}` : "Đúng số tiền phải đóng"}</small>
                  </div>
                  <div className="settlement-received-input">
                    <input
                      inputMode="numeric"
                      aria-label={`Tiền thực nhận của ${name}`}
                      value={receivedInputs[entry.id] ?? String(entry.actualPaidAmount)}
                      onChange={(event) => setReceivedInputs((current) => ({ ...current, [entry.id]: event.target.value.replace(/\D/g, "") }))}
                      onKeyDown={(event) => { if (event.key === "Enter") void saveReceivedAmount(entry); }}
                      disabled={isUpdating}
                    />
                    <span>đ</span>
                    <button
                      type="button"
                      disabled={isUpdating || Number(receivedInputs[entry.id]) === entry.actualPaidAmount}
                      onClick={() => void saveReceivedAmount(entry)}
                    >
                      Lưu
                    </button>
                  </div>
                </div>
              ) : null}
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
            openComposer("summary");
          }
        }}>
          {unpaidCount > 0 ? <BellRing size={19} aria-hidden="true" /> : <Volleyball size={19} aria-hidden="true" />}
          <span>{unpaidCount > 0 ? "Réo chưa đóng" : "Tổng kết"}</span>
        </button>
        <button type="button" onClick={() => openComposer("compose")}><MessageSquareText size={19} aria-hidden="true" /><span>Soạn tin</span></button>
        <button type="button" onClick={() => openComposer("schedule")}><Clock3 size={19} aria-hidden="true" /><span>Hẹn giờ</span></button>
      </div>

      {composerOpen ? (
        <div className="settlement-sheet-backdrop" role="presentation" onMouseDown={(event) => { if (event.currentTarget === event.target) closeComposer(); }}>
          <section ref={composerDialogRef} className="settlement-sheet" role="dialog" aria-modal="true" aria-labelledby="settlement-composer-title">
            <div className="settlement-sheet-handle" />
            <div className="settlement-sheet-heading">
              <div><span className="settlement-kicker"><MessageSquareText size={15} aria-hidden="true" /> ZYRO • Zalo</span><h3 id="settlement-composer-title">{actionKind === "Summary" ? "Gửi ảnh tổng kết" : "Soạn tin nhắn"}</h3></div>
              <button className="settlement-icon-button" type="button" aria-label="Đóng" onClick={closeComposer}><X size={20} /></button>
            </div>

            {error ? <div className="settlement-notice settlement-error-notice" role="alert">{error}</div> : null}
            {notice ? <div className="settlement-notice" role="status">{notice}</div> : null}

            <div className="settlement-presets" aria-label="Mẫu tin nhanh">
              {quickPresets.map((preset) => {
                const isActive = actionKind === preset.kind && audience === preset.audience && message === preset.message;
                return <button type="button" aria-pressed={isActive} className={isActive ? "active" : ""} key={preset.id} onClick={() => applyPreset(preset)}>{preset.label}</button>;
              })}
            </div>

            {actionKind === "Summary" ? (
              <div className="settlement-summary-preview">
                <div className="settlement-summary-preview-head">
                  <span className="settlement-zyro-mark" aria-hidden="true"><Volleyball size={22} /></span>
                  <div><strong>Ảnh tổng kết ZYRO</strong><small>Gửi vào nhóm, không @all và không mention thành viên.</small></div>
                </div>
                <div className="settlement-summary-preview-grid">
                  <div><span>Đã đóng</span><strong>{settlement.paidCount}/{settlement.totalCount}</strong></div>
                  <div><span>Thực nhận</span><strong>{formatMoney(settlement.totalReceived)}</strong></div>
                  <div><span>Dư trận</span><strong>{settlement.settlementSurplus > 0 ? "+" : ""}{formatMoney(settlement.settlementSurplus)}</strong></div>
                  <div><span>Quỹ Zyro</span><strong>{formatMoney(settlement.groupFundBalance)}</strong></div>
                </div>
                <p>Ảnh được render lại từ dữ liệu mới nhất đúng lúc bot gửi, kể cả khi bạn hẹn giờ.</p>
              </div>
            ) : (
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
            )}

            {actionKind !== "Summary" && audience === "selected" ? (
              <div className="settlement-selected-list">
                {sortedEntries.map((entry) => (
                  <label key={entry.id}><input type="checkbox" checked={selectedIds.includes(entry.id)} onChange={() => toggleSelected(entry.id)} /><span>{entryName(entry)}</span></label>
                ))}
              </div>
            ) : null}

            {actionKind !== "Summary" ? (
              <label className="settlement-composer-field">
                <span>Nội dung</span>
                <textarea rows={5} value={message} onChange={(event) => { setActionKind("Custom"); setMessage(event.target.value); }} placeholder="Nhập lời nhắn…" />
              </label>
            ) : null}

            {actionKind !== "Summary" ? <div className="settlement-composer-options">
              {settlement.paymentQrImageUrl ? (
                <button type="button" aria-pressed={attachQr} className={attachQr ? "active" : ""} onClick={() => setAttachQr((value) => !value)}>
                  <QrCode size={18} aria-hidden="true" /> Đính kèm QR
                  {attachQr ? <Check size={16} aria-hidden="true" /> : null}
                </button>
              ) : (
                <button type="button" disabled={qrUploading} onClick={() => qrFileInputRef.current?.click()}>
                  <QrCode size={18} aria-hidden="true" /> {qrUploading ? "Đang tải QR…" : "Thêm QR để đính kèm"}
                </button>
              )}
              <button type="button" disabled={!settlement.paymentQrImageUrl || qrUploading || qrSending} onClick={() => void sendPaymentQrNow()}>
                <Send size={18} aria-hidden="true" /> {qrSending ? "Đang gửi…" : "Gửi QR ngay"}
              </button>
            </div> : null}

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

            <button className={`settlement-submit ${actionKind === "Summary" ? "summary" : ""}`} type="button" disabled={busy || (actionKind !== "Summary" && !message.trim()) || (actionKind !== "Summary" && audience === "selected" && selectedIds.length === 0)} onClick={() => void submitComposer()}>
              {actionKind === "Summary" ? <Volleyball size={18} aria-hidden="true" /> : deliveryMode === "now" ? <Send size={18} aria-hidden="true" /> : <Clock3 size={18} aria-hidden="true" />}
              {busy ? "Đang gửi…" : actionKind === "Summary" ? (deliveryMode === "now" ? "Gửi ảnh tổng kết" : `Hẹn ảnh sau ${delayLabel}`) : deliveryMode === "now" ? "Gửi ngay" : `Hẹn sau ${delayLabel}`}
            </button>
          </section>
        </div>
      ) : null}
    </section>
  );
}
