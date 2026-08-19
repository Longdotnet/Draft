import { useEffect, useMemo, useState } from "react";
import { AlertTriangle, Brain, CheckCircle2, Power, RefreshCw, ShieldCheck, TestTube2, Users, XCircle } from "lucide-react";
import { ApiRequestError, apiFetch } from "../api/dbClient";

type AutoSessionHealth = {
  connectionStatus: string;
  lastPollEventAt: string | null;
  lastReconcileAt: string | null;
  lastSuccessAt: string | null;
  lastErrorAt: string | null;
  lastError: string | null;
  consecutiveFailures: number;
  nextRetryAt: string | null;
};

type LearningSignal = {
  id: string;
  pollId: string;
  signalType: string;
  dayKey: string | null;
  originalStartTime: string | null;
  actualStartTime: string | null;
  suggestedRuleType: string | null;
  suggestedMinutes: number | null;
  status: string;
  reviewNote: string | null;
  createdAt: string;
  updatedAt: string;
};

type OrganizerCandidate = {
  zaloUserId: string;
  displayName: string;
  zaloRole: string;
  isCurrentOrganizer: boolean;
  trustedBackup: boolean;
  isFallbackByDefault: boolean;
};

type AutoSessionGroup = {
  id: string;
  groupName: string;
  groupId: string;
  zaloConnectionId: string;
  autoSessionEnabled: boolean;
  requireOrganizerApproval: boolean;
  defaultTeamSize: number;
  defaultTotalSets: number;
  defaultStartTime: string;
  assumePmForHourUnder12: boolean;
  defaultLocation: string | null;
  botEnabledForCreatedSessions: boolean;
  globalEnabled: boolean;
  rolloutMode: "Disabled" | "PreviewOnly" | "Live" | string;
  health: AutoSessionHealth | null;
  learningSignals: LearningSignal[] | null;
  pendingLearningCount: number;
  organizerCandidates: OrganizerCandidate[] | null;
};

type UpdateExtras = {
  globalEnabled?: boolean;
  rolloutMode?: string;
  learningSignalId?: string;
  learningDecision?: "Approved" | "Rejected";
  learningReviewNote?: string | null;
  trustedOrganizerZaloUserId?: string;
  trustedOrganizerDisplayName?: string;
  trustedOrganizerEnabled?: boolean;
};

function formatDate(value: string | null | undefined) {
  if (!value) return "—";
  const date = new Date(value);
  if (Number.isNaN(date.getTime())) return "—";
  return new Intl.DateTimeFormat("vi-VN", {
    day: "2-digit",
    month: "2-digit",
    hour: "2-digit",
    minute: "2-digit",
  }).format(date);
}

function formatMinutes(value: number | null) {
  if (value === null) return "—";
  const hour = Math.floor(value / 60);
  const minute = value % 60;
  return `${String(hour).padStart(2, "0")}:${String(minute).padStart(2, "0")}`;
}

function rolloutDescription(mode: string) {
  if (mode === "Disabled") return "Không đọc poll mới cho Auto Session.";
  if (mode === "PreviewOnly") return "Canary: hiểu poll và gửi preview nhưng tuyệt đối chưa tạo website.";
  return "Production: gửi preview cho đúng người tạo poll; chỉ tạo website sau khi họ reply xác nhận.";
}

export function ZaloAutoSessionOperationsPanel() {
  const [token, setToken] = useState(() => localStorage.getItem("volleyDraftToken"));
  const [groups, setGroups] = useState<AutoSessionGroup[]>([]);
  const [selectedId, setSelectedId] = useState("");
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
      setGroups([]);
      setSelectedId("");
      return;
    }
    void load(token);
  }, [token]);

  const selected = useMemo(
    () => groups.find((group) => group.id === selectedId) ?? groups[0] ?? null,
    [groups, selectedId],
  );

  if (!token) return null;

  async function load(authToken = token) {
    if (!authToken) return;
    setBusy(true);
    try {
      const next = await apiFetch<AutoSessionGroup[]>("/zalo/auto-session-groups", { token: authToken });
      setGroups(next);
      setSelectedId((current) => (next.some((item) => item.id === current) ? current : next[0]?.id ?? ""));
      setMessage(null);
    } catch (error) {
      setMessage(error instanceof ApiRequestError ? error.message : "Không tải được Auto Session Operations.");
    } finally {
      setBusy(false);
    }
  }

  function buildBody(group: AutoSessionGroup, extras: UpdateExtras) {
    return {
      autoSessionEnabled: group.autoSessionEnabled,
      requireOrganizerApproval: group.requireOrganizerApproval,
      defaultTeamSize: group.defaultTeamSize,
      defaultTotalSets: group.defaultTotalSets,
      defaultStartTime: group.defaultStartTime,
      assumePmForHourUnder12: group.assumePmForHourUnder12,
      defaultLocation: group.defaultLocation,
      botEnabledForCreatedSessions: group.botEnabledForCreatedSessions,
      ...extras,
    };
  }

  async function update(extras: UpdateExtras, successMessage: string) {
    if (!token || !selected) return;
    setBusy(true);
    try {
      await apiFetch<AutoSessionGroup>(`/zalo/auto-session-groups/${selected.id}`, {
        method: "PUT",
        token,
        body: buildBody(selected, extras),
      });
      await load(token);
      setMessage(successMessage);
    } catch (error) {
      setMessage(error instanceof ApiRequestError ? error.message : "Không cập nhật được Auto Session Operations.");
      setBusy(false);
    }
  }

  const panelStyle = {
    margin: "24px auto 0",
    maxWidth: 1180,
    padding: 20,
    borderRadius: 18,
    border: "1px solid rgba(168, 85, 247, 0.28)",
    background: "rgba(15, 23, 42, 0.78)",
    color: "#e2e8f0",
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
    justifyContent: "center",
    gap: 7,
    border: 0,
    borderRadius: 10,
    padding: "9px 12px",
    cursor: busy ? "wait" : "pointer",
    fontWeight: 700,
  } as const;

  return (
    <section style={panelStyle}>
      <div style={{ display: "flex", justifyContent: "space-between", gap: 14, alignItems: "flex-start", flexWrap: "wrap" }}>
        <div>
          <div style={{ display: "flex", gap: 9, alignItems: "center" }}>
            <ShieldCheck size={22} />
            <h2 style={{ margin: 0 }}>Auto Session Operations</h2>
          </div>
          <p style={{ margin: "7px 0 0", color: "#94a3b8", maxWidth: 780 }}>
            Goal: trưởng/phó tạo poll → hệ thống hiểu ngữ cảnh → preview đúng người tạo poll trên Zalo → người đó reply xác nhận → mới tạo website. Không có waitlist tự động; slot trống thuộc về người vote nhanh hơn.
          </p>
        </div>
        <button type="button" disabled={busy} onClick={() => void load()} style={{ ...buttonStyle, background: "#334155", color: "white" }}>
          <RefreshCw size={15} /> Làm mới
        </button>
      </div>

      {groups.length === 0 ? (
        <div style={{ marginTop: 16, padding: 14, borderRadius: 12, background: "rgba(30,41,59,.55)", color: "#94a3b8" }}>
          Chưa có tracked group. Hãy thêm group ở panel Auto Session settings trước.
        </div>
      ) : null}

      {selected ? (
        <>
          <div style={{ display: "grid", gridTemplateColumns: "minmax(220px,1.2fr) repeat(2,minmax(180px,1fr))", gap: 12, marginTop: 18 }}>
            <label>
              <span style={{ display: "block", marginBottom: 6, color: "#cbd5e1" }}>Group</span>
              <select value={selected.id} onChange={(event) => setSelectedId(event.target.value)} style={inputStyle}>
                {groups.map((group) => <option key={group.id} value={group.id}>{group.groupName}</option>)}
              </select>
            </label>

            <div style={{ padding: 12, borderRadius: 12, border: `1px solid ${selected.globalEnabled ? "rgba(34,197,94,.4)" : "rgba(239,68,68,.4)"}`, background: "rgba(30,41,59,.55)" }}>
              <div style={{ display: "flex", gap: 7, alignItems: "center", fontWeight: 750 }}>
                <Power size={16} /> Global kill switch
              </div>
              <div style={{ marginTop: 6, color: selected.globalEnabled ? "#86efac" : "#fca5a5" }}>
                {selected.globalEnabled ? "ON — Auto Session được phép chạy" : "OFF — chặn preview và create"}
              </div>
              <button
                type="button"
                disabled={busy}
                onClick={() => void update({ globalEnabled: !selected.globalEnabled }, selected.globalEnabled ? "Đã tắt Auto Session toàn hệ thống." : "Đã bật Auto Session toàn hệ thống.")}
                style={{ ...buttonStyle, marginTop: 9, background: selected.globalEnabled ? "#7f1d1d" : "#166534", color: "white" }}
              >
                {selected.globalEnabled ? "Tắt toàn hệ thống" : "Bật lại"}
              </button>
            </div>

            <div style={{ padding: 12, borderRadius: 12, border: "1px solid rgba(245,158,11,.32)", background: "rgba(30,41,59,.55)" }}>
              <div style={{ display: "flex", gap: 7, alignItems: "center", fontWeight: 750 }}>
                <TestTube2 size={16} /> Rollout group
              </div>
              <select
                value={selected.rolloutMode}
                disabled={busy}
                onChange={(event) => void update({ rolloutMode: event.target.value }, `Đã chuyển ${selected.groupName} sang ${event.target.value}.`)}
                style={{ ...inputStyle, marginTop: 7 }}
              >
                <option value="Disabled">Disabled</option>
                <option value="PreviewOnly">PreviewOnly (canary)</option>
                <option value="Live">Live</option>
              </select>
              <div style={{ color: "#94a3b8", fontSize: 12, marginTop: 6 }}>{rolloutDescription(selected.rolloutMode)}</div>
            </div>
          </div>

          <div style={{ marginTop: 14, padding: 14, borderRadius: 13, background: "rgba(30,41,59,.48)" }}>
            <strong>Health / retry</strong>
            <div style={{ display: "grid", gridTemplateColumns: "repeat(auto-fit,minmax(160px,1fr))", gap: 10, marginTop: 10, fontSize: 13 }}>
              <div><span style={{ color: "#64748b" }}>Zalo connection</span><br /><strong>{selected.health?.connectionStatus ?? "—"}</strong></div>
              <div><span style={{ color: "#64748b" }}>Poll event cuối</span><br /><strong>{formatDate(selected.health?.lastPollEventAt)}</strong></div>
              <div><span style={{ color: "#64748b" }}>Reconcile cuối</span><br /><strong>{formatDate(selected.health?.lastReconcileAt)}</strong></div>
              <div><span style={{ color: "#64748b" }}>Success cuối</span><br /><strong>{formatDate(selected.health?.lastSuccessAt)}</strong></div>
              <div><span style={{ color: "#64748b" }}>Failure liên tiếp</span><br /><strong>{selected.health?.consecutiveFailures ?? 0}</strong></div>
              <div><span style={{ color: "#64748b" }}>Retry tiếp theo</span><br /><strong>{formatDate(selected.health?.nextRetryAt)}</strong></div>
            </div>
            {selected.health?.lastError ? (
              <div style={{ display: "flex", gap: 7, marginTop: 10, padding: 10, borderRadius: 10, background: "rgba(239,68,68,.1)", color: "#fca5a5" }}>
                <AlertTriangle size={15} style={{ flex: "0 0 auto", marginTop: 1 }} />
                <span>{selected.health.lastError}</span>
              </div>
            ) : null}
          </div>

          <div style={{ marginTop: 14, padding: 14, borderRadius: 13, background: "rgba(30,41,59,.48)" }}>
            <div style={{ display: "flex", gap: 7, alignItems: "center" }}>
              <Users size={17} /> <strong>Trusted Auto Session operators</strong>
            </div>
            <p style={{ color: "#94a3b8", fontSize: 13, margin: "7px 0 0" }}>
              Người tạo poll giữ quyền xử lý poll của họ. Trưởng nhóm là fallback mặc định. Phó/admin khác chỉ được takeover sau escalation khi bạn bật <strong>Trusted Backup</strong>; admin Zalo không được tick sẽ chỉ là bystander và bot bỏ qua chat linh tinh của họ.
            </p>

            <div style={{ display: "grid", gap: 9, marginTop: 12 }}>
              {(selected.organizerCandidates ?? []).length === 0 ? (
                <div style={{ color: "#64748b" }}>
                  Chưa đọc được danh sách trưởng/phó hiện tại. Kiểm tra Zalo connection rồi bấm Làm mới.
                </div>
              ) : (selected.organizerCandidates ?? []).map((candidate) => {
                const staleTrusted = candidate.trustedBackup && !candidate.isCurrentOrganizer;
                const canToggle = !candidate.isFallbackByDefault && (candidate.isCurrentOrganizer || staleTrusted);
                const roleLabel = candidate.isFallbackByDefault
                  ? "Trưởng nhóm · fallback mặc định"
                  : candidate.isCurrentOrganizer
                    ? "Phó/Admin Zalo"
                    : "Không còn là admin Zalo";

                return (
                  <div
                    key={candidate.zaloUserId}
                    style={{
                      display: "flex",
                      alignItems: "center",
                      justifyContent: "space-between",
                      gap: 12,
                      flexWrap: "wrap",
                      padding: 11,
                      borderRadius: 11,
                      border: staleTrusted
                        ? "1px solid rgba(245,158,11,.35)"
                        : "1px solid rgba(148,163,184,.16)",
                      background: "rgba(15,23,42,.5)",
                    }}
                  >
                    <div style={{ minWidth: 220 }}>
                      <strong>{candidate.displayName || candidate.zaloUserId}</strong>
                      <div style={{ color: staleTrusted ? "#fcd34d" : "#94a3b8", fontSize: 12, marginTop: 3 }}>
                        {roleLabel} · {candidate.zaloUserId}
                      </div>
                    </div>

                    {candidate.isFallbackByDefault ? (
                      <span style={{ color: "#86efac", fontSize: 12, fontWeight: 750 }}>
                        Fallback mặc định
                      </span>
                    ) : (
                      <button
                        type="button"
                        disabled={busy || !canToggle}
                        onClick={() => void update(
                          {
                            trustedOrganizerZaloUserId: candidate.zaloUserId,
                            trustedOrganizerDisplayName: candidate.displayName,
                            trustedOrganizerEnabled: !candidate.trustedBackup,
                          },
                          candidate.trustedBackup
                            ? `Đã bỏ Trusted Backup của ${candidate.displayName}.`
                            : `Đã bật Trusted Backup cho ${candidate.displayName}.`,
                        )}
                        style={{
                          ...buttonStyle,
                          background: candidate.trustedBackup ? "#7f1d1d" : "#166534",
                          color: "white",
                          opacity: canToggle ? 1 : 0.55,
                        }}
                      >
                        {candidate.trustedBackup ? "Tắt Trusted Backup" : "Bật Trusted Backup"}
                      </button>
                    )}
                  </div>
                );
              })}
            </div>

            <div style={{ marginTop: 9, color: "#64748b", fontSize: 12 }}>
              Safety: Trust chỉ được đổi từ web admin. AI/chat không thể tự cấp quyền. Khi tạo website, backend kiểm lại cả quyền Zalo hiện tại lẫn Trusted Backup một lần nữa.
            </div>
          </div>

          <div style={{ marginTop: 14, padding: 14, borderRadius: 13, background: "rgba(30,41,59,.48)" }}>
            <div style={{ display: "flex", justifyContent: "space-between", gap: 10, flexWrap: "wrap" }}>
              <div style={{ display: "flex", gap: 7, alignItems: "center" }}>
                <Brain size={17} /> <strong>Controlled learning</strong>
              </div>
              <span style={{ color: selected.pendingLearningCount > 0 ? "#fcd34d" : "#94a3b8", fontSize: 13 }}>
                {selected.pendingLearningCount} signal chờ duyệt
              </span>
            </div>
            <p style={{ color: "#94a3b8", fontSize: 13, margin: "7px 0 0" }}>
              Organizer sửa preview được ghi thành tín hiệu. Chỉ signal “default_day_time” sau khi admin Approved mới được dùng cho poll tương lai; correction một lần không tự biến thành rule.
            </p>

            <div style={{ display: "grid", gap: 10, marginTop: 12 }}>
              {(selected.learningSignals ?? []).length === 0 ? (
                <div style={{ color: "#64748b" }}>Chưa có correction nào cần học.</div>
              ) : (selected.learningSignals ?? []).map((signal) => (
                <div key={signal.id} style={{ padding: 11, borderRadius: 11, border: "1px solid rgba(148,163,184,.16)", background: "rgba(15,23,42,.5)" }}>
                  <div style={{ display: "flex", justifyContent: "space-between", gap: 10, flexWrap: "wrap" }}>
                    <div>
                      <strong>{signal.signalType}</strong>{signal.dayKey ? ` · ${signal.dayKey}` : ""}
                      <div style={{ color: "#64748b", fontSize: 12, marginTop: 3 }}>
                        Poll {signal.pollId} · {formatDate(signal.createdAt)}
                      </div>
                    </div>
                    <span style={{ color: signal.status === "Pending" ? "#fcd34d" : signal.status === "Approved" ? "#86efac" : "#fca5a5", fontSize: 12, fontWeight: 700 }}>
                      {signal.status}
                    </span>
                  </div>
                  <div style={{ marginTop: 7, color: "#cbd5e1", fontSize: 13 }}>
                    {signal.originalStartTime ? <>AI/rule preview: {formatDate(signal.originalStartTime)} · </> : null}
                    {signal.actualStartTime ? <>Organizer chọn: {formatDate(signal.actualStartTime)}</> : "Organizer bỏ option này"}
                  </div>
                  {signal.suggestedRuleType ? (
                    <div style={{ marginTop: 5, color: "#93c5fd", fontSize: 13 }}>
                      Rule đề xuất: {signal.suggestedRuleType} {signal.dayKey ?? ""} → {formatMinutes(signal.suggestedMinutes)}
                    </div>
                  ) : (
                    <div style={{ marginTop: 5, color: "#64748b", fontSize: 12 }}>Chỉ lưu feedback, không có rule tự động để promote.</div>
                  )}
                  {signal.status === "Pending" ? (
                    <div style={{ display: "flex", gap: 8, marginTop: 9, flexWrap: "wrap" }}>
                      <button
                        type="button"
                        disabled={busy}
                        onClick={() => void update({ learningSignalId: signal.id, learningDecision: "Approved" }, "Đã duyệt learning signal. Rule promotable sẽ được dùng cho preview tương lai.")}
                        style={{ ...buttonStyle, background: "#166534", color: "white" }}
                      >
                        <CheckCircle2 size={14} /> Approve
                      </button>
                      <button
                        type="button"
                        disabled={busy}
                        onClick={() => void update({ learningSignalId: signal.id, learningDecision: "Rejected" }, "Đã từ chối learning signal; hệ thống sẽ không học từ correction này.")}
                        style={{ ...buttonStyle, background: "#7f1d1d", color: "white" }}
                      >
                        <XCircle size={14} /> Reject
                      </button>
                    </div>
                  ) : null}
                </div>
              ))}
            </div>
          </div>
        </>
      ) : null}

      {message ? (
        <div style={{ marginTop: 14, padding: 10, borderRadius: 10, background: "rgba(51,65,85,.62)", color: "#e2e8f0" }}>
          {message}
        </div>
      ) : null}
    </section>
  );
}
