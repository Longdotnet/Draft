import { useEffect, useMemo, useState } from "react";
import { Bot, Clock3, Link2, MapPin, RefreshCw, Save, ShieldCheck, Users } from "lucide-react";
import {
  ApiRequestError,
  apiFetch,
  type ZaloConnectionResponse,
  type ZaloGroupResponse,
} from "../api/dbClient";

type AutoSessionGroup = {
  id: string;
  adminUserId: string;
  zaloConnectionId: string;
  connectionDisplayName: string;
  accountZaloId: string;
  groupId: string;
  groupName: string;
  autoSessionEnabled: boolean;
  requireOrganizerApproval: boolean;
  defaultTeamCount: number;
  defaultTeamSize: number;
  capacity: number;
  defaultTotalSets: number;
  defaultStartTime: string;
  assumePmForHourUnder12: boolean;
  defaultLocation: string | null;
  botEnabledForCreatedSessions: boolean;
  existingSessionCount: number;
  createdAt: string;
  updatedAt: string;
};

type FormState = {
  autoSessionEnabled: boolean;
  requireOrganizerApproval: boolean;
  defaultTeamSize: number;
  defaultTotalSets: number;
  defaultStartTime: string;
  assumePmForHourUnder12: boolean;
  defaultLocation: string;
  botEnabledForCreatedSessions: boolean;
};

const emptyForm: FormState = {
  autoSessionEnabled: true,
  requireOrganizerApproval: true,
  defaultTeamSize: 6,
  defaultTotalSets: 4,
  defaultStartTime: "17:30",
  assumePmForHourUnder12: true,
  defaultLocation: "",
  botEnabledForCreatedSessions: true,
};

function normalizeId(value: string) {
  const normalized = value.trim();
  return normalized.endsWith("_0") ? normalized.slice(0, -2) : normalized;
}

function formatUpdatedAt(value: string) {
  const date = new Date(value);
  if (Number.isNaN(date.getTime())) return "—";
  return new Intl.DateTimeFormat("vi-VN", {
    day: "2-digit",
    month: "2-digit",
    hour: "2-digit",
    minute: "2-digit",
  }).format(date);
}

export function ZaloAutoSessionAdminPanel() {
  const [token, setToken] = useState(() => localStorage.getItem("volleyDraftToken"));
  const [trackedGroups, setTrackedGroups] = useState<AutoSessionGroup[]>([]);
  const [connections, setConnections] = useState<ZaloConnectionResponse[]>([]);
  const [candidateGroups, setCandidateGroups] = useState<ZaloGroupResponse[]>([]);
  const [selectedTrackedId, setSelectedTrackedId] = useState("");
  const [newConnectionId, setNewConnectionId] = useState("");
  const [newGroupId, setNewGroupId] = useState("");
  const [form, setForm] = useState<FormState>(emptyForm);
  const [busy, setBusy] = useState(false);
  const [loadingGroups, setLoadingGroups] = useState(false);
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
      setTrackedGroups([]);
      setConnections([]);
      setCandidateGroups([]);
      setSelectedTrackedId("");
      return;
    }
    void loadOverview(token);
  }, [token]);

  useEffect(() => {
    if (!token || !newConnectionId) {
      setCandidateGroups([]);
      setNewGroupId("");
      return;
    }
    void loadCandidateGroups(token, newConnectionId);
  }, [token, newConnectionId]);

  const selectedTracked = useMemo(
    () => trackedGroups.find((item) => item.id === selectedTrackedId) ?? null,
    [trackedGroups, selectedTrackedId],
  );

  const availableGroups = useMemo(() => {
    const trackedKeys = new Set(
      trackedGroups
        .filter((item) => item.zaloConnectionId === newConnectionId)
        .map((item) => normalizeId(item.groupId)),
    );
    return candidateGroups.filter((item) => !trackedKeys.has(normalizeId(item.id)));
  }, [candidateGroups, newConnectionId, trackedGroups]);

  useEffect(() => {
    if (!selectedTracked) {
      setForm(emptyForm);
      return;
    }
    setForm({
      autoSessionEnabled: selectedTracked.autoSessionEnabled,
      requireOrganizerApproval: selectedTracked.requireOrganizerApproval,
      defaultTeamSize: selectedTracked.defaultTeamSize,
      defaultTotalSets: selectedTracked.defaultTotalSets,
      defaultStartTime: selectedTracked.defaultStartTime,
      assumePmForHourUnder12: selectedTracked.assumePmForHourUnder12,
      defaultLocation: selectedTracked.defaultLocation ?? "",
      botEnabledForCreatedSessions: selectedTracked.botEnabledForCreatedSessions,
    });
  }, [selectedTracked]);

  useEffect(() => {
    if (availableGroups.some((item) => normalizeId(item.id) === normalizeId(newGroupId))) return;
    setNewGroupId(availableGroups[0]?.id ?? "");
  }, [availableGroups, newGroupId]);

  if (!token) return null;

  async function loadOverview(authToken = token) {
    if (!authToken) return;
    setBusy(true);
    try {
      const [nextTracked, nextConnections] = await Promise.all([
        apiFetch<AutoSessionGroup[]>("/zalo/auto-session-groups", { token: authToken }),
        apiFetch<ZaloConnectionResponse[]>("/zalo/connections", { token: authToken }),
      ]);
      setTrackedGroups(nextTracked);
      setConnections(nextConnections.filter((item) => item.status === "Connected"));
      setSelectedTrackedId((current) =>
        nextTracked.some((item) => item.id === current) ? current : nextTracked[0]?.id ?? "",
      );
      setNewConnectionId((current) =>
        nextConnections.some((item) => item.id === current && item.status === "Connected")
          ? current
          : nextConnections.find((item) => item.status === "Connected")?.id ?? "",
      );
      setMessage(null);
    } catch (error) {
      setMessage(error instanceof ApiRequestError ? error.message : "Không tải được cấu hình Auto Session.");
    } finally {
      setBusy(false);
    }
  }

  async function loadCandidateGroups(authToken: string, connectionId: string) {
    setLoadingGroups(true);
    try {
      const groups = await apiFetch<ZaloGroupResponse[]>(`/zalo/connections/${connectionId}/groups`, {
        token: authToken,
      });
      setCandidateGroups(groups);
    } catch (error) {
      setCandidateGroups([]);
      setMessage(error instanceof ApiRequestError ? error.message : "Không đọc được danh sách group Zalo.");
    } finally {
      setLoadingGroups(false);
    }
  }

  async function addTrackedGroup() {
    if (!token || !newConnectionId || !newGroupId) return;
    setBusy(true);
    try {
      const created = await apiFetch<AutoSessionGroup>("/zalo/auto-session-groups", {
        method: "POST",
        token,
        body: { connectionId: newConnectionId, groupId: newGroupId },
      });
      setTrackedGroups((current) => [
        created,
        ...current.filter((item) => item.id !== created.id),
      ]);
      setSelectedTrackedId(created.id);
      setMessage(`Đã bật theo dõi ${created.groupName}. Listener Zalo đã được reconcile.`);
      await loadCandidateGroups(token, newConnectionId);
    } catch (error) {
      setMessage(error instanceof ApiRequestError ? error.message : "Không bật được Auto Session cho group này.");
    } finally {
      setBusy(false);
    }
  }

  async function saveSettings() {
    if (!token || !selectedTracked) return;
    setBusy(true);
    try {
      const updated = await apiFetch<AutoSessionGroup>(
        `/zalo/auto-session-groups/${selectedTracked.id}`,
        {
          method: "PUT",
          token,
          body: {
            autoSessionEnabled: form.autoSessionEnabled,
            requireOrganizerApproval: form.requireOrganizerApproval,
            defaultTeamSize: Number(form.defaultTeamSize),
            defaultTotalSets: Number(form.defaultTotalSets),
            defaultStartTime: form.defaultStartTime,
            assumePmForHourUnder12: form.assumePmForHourUnder12,
            defaultLocation: form.defaultLocation.trim() || null,
            botEnabledForCreatedSessions: form.botEnabledForCreatedSessions,
          },
        },
      );
      setTrackedGroups((current) => current.map((item) => (item.id === updated.id ? updated : item)));
      setMessage(
        updated.autoSessionEnabled
          ? `Đã lưu ${updated.groupName}. Poll mới có thể được phát hiện tự động.`
          : `Đã tắt Auto Session cho ${updated.groupName}; listener đã được cập nhật.`,
      );
    } catch (error) {
      setMessage(error instanceof ApiRequestError ? error.message : "Không lưu được cấu hình Auto Session.");
    } finally {
      setBusy(false);
    }
  }

  const cardStyle = {
    margin: "24px auto 0",
    maxWidth: 1180,
    padding: 20,
    borderRadius: 18,
    border: "1px solid rgba(34, 197, 94, 0.24)",
    background: "rgba(15, 23, 42, 0.76)",
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
    justifyContent: "center",
    gap: 7,
    border: 0,
    borderRadius: 10,
    padding: "10px 14px",
    cursor: busy ? "wait" : "pointer",
    fontWeight: 700,
  } as const;
  const toggleRowStyle = {
    display: "flex",
    gap: 10,
    alignItems: "flex-start",
    padding: "10px 12px",
    borderRadius: 12,
    border: "1px solid rgba(148, 163, 184, 0.18)",
    background: "rgba(30, 41, 59, 0.5)",
  } as const;

  return (
    <section style={cardStyle}>
      <div style={{ display: "flex", justifyContent: "space-between", gap: 16, alignItems: "flex-start", flexWrap: "wrap" }}>
        <div>
          <div style={{ display: "flex", gap: 9, alignItems: "center" }}>
            <Bot size={22} />
            <h2 style={{ margin: 0 }}>Auto Session từ Zalo Poll</h2>
          </div>
          <p style={{ margin: "8px 0 0", color: "#94a3b8", maxWidth: 760 }}>
            Zalo là nguồn sự kiện, AI chỉ hiểu nội dung poll. Khi bật xác nhận, chỉ reply đúng tin đề xuất của bot từ trưởng/phó group mới được tạo session.
          </p>
        </div>
        <button
          type="button"
          disabled={busy}
          onClick={() => void loadOverview()}
          style={{ ...buttonStyle, background: "#334155", color: "#f8fafc" }}
        >
          <RefreshCw size={16} /> Làm mới
        </button>
      </div>

      <div style={{ marginTop: 18, padding: 16, borderRadius: 14, background: "rgba(30, 41, 59, 0.55)" }}>
        <div style={{ display: "flex", gap: 8, alignItems: "center", marginBottom: 10 }}>
          <Link2 size={18} />
          <strong>Theo dõi group Zalo mới</strong>
        </div>
        <div style={gridStyle}>
          <label>
            <span style={{ display: "block", marginBottom: 6, color: "#cbd5e1" }}>Tài khoản Zalo</span>
            <select
              value={newConnectionId}
              onChange={(event) => setNewConnectionId(event.target.value)}
              style={inputStyle}
            >
              {connections.length === 0 ? <option value="">Chưa có kết nối Connected</option> : null}
              {connections.map((item) => (
                <option key={item.id} value={item.id}>
                  {item.displayName} · {item.accountZaloId}
                </option>
              ))}
            </select>
          </label>
          <label>
            <span style={{ display: "block", marginBottom: 6, color: "#cbd5e1" }}>Group chưa theo dõi</span>
            <select
              value={newGroupId}
              onChange={(event) => setNewGroupId(event.target.value)}
              style={inputStyle}
              disabled={loadingGroups || availableGroups.length === 0}
            >
              {loadingGroups ? <option value="">Đang tải group…</option> : null}
              {!loadingGroups && availableGroups.length === 0 ? <option value="">Không còn group mới</option> : null}
              {availableGroups.map((item) => (
                <option key={item.id} value={item.id}>
                  {item.name} · {item.totalMembers} thành viên
                </option>
              ))}
            </select>
          </label>
          <div style={{ display: "flex", alignItems: "end" }}>
            <button
              type="button"
              disabled={busy || !newConnectionId || !newGroupId}
              onClick={() => void addTrackedGroup()}
              style={{ ...buttonStyle, width: "100%", background: "#16a34a", color: "white" }}
            >
              <Link2 size={16} /> Bật theo dõi group
            </button>
          </div>
        </div>
      </div>

      <div style={{ marginTop: 18 }}>
        <label>
          <span style={{ display: "block", marginBottom: 6, color: "#cbd5e1" }}>Group đang cấu hình</span>
          <select
            value={selectedTrackedId}
            onChange={(event) => setSelectedTrackedId(event.target.value)}
            style={inputStyle}
          >
            {trackedGroups.length === 0 ? <option value="">Chưa có group Auto Session</option> : null}
            {trackedGroups.map((item) => (
              <option key={item.id} value={item.id}>
                {item.autoSessionEnabled ? "🟢" : "⚪"} {item.groupName} · {item.capacity} slot
              </option>
            ))}
          </select>
        </label>
      </div>

      {selectedTracked ? (
        <>
          <div style={{ ...gridStyle, marginTop: 16 }}>
            <div style={toggleRowStyle}>
              <input
                type="checkbox"
                checked={form.autoSessionEnabled}
                onChange={(event) => setForm((current) => ({ ...current, autoSessionEnabled: event.target.checked }))}
              />
              <div>
                <strong>Auto Session</strong>
                <div style={{ color: "#94a3b8", fontSize: 13 }}>Nghe poll mới và tạo proposal/session tự động.</div>
              </div>
            </div>
            <div style={toggleRowStyle}>
              <input
                type="checkbox"
                checked={form.requireOrganizerApproval}
                onChange={(event) => setForm((current) => ({ ...current, requireOrganizerApproval: event.target.checked }))}
              />
              <div>
                <strong>Trưởng/phó phải xác nhận</strong>
                <div style={{ color: "#94a3b8", fontSize: 13 }}>Khuyến nghị bật để AI không có quyền ghi DB trực tiếp.</div>
              </div>
            </div>
            <div style={toggleRowStyle}>
              <input
                type="checkbox"
                checked={form.botEnabledForCreatedSessions}
                onChange={(event) => setForm((current) => ({ ...current, botEnabledForCreatedSessions: event.target.checked }))}
              />
              <div>
                <strong>Bật bot cho session mới</strong>
                <div style={{ color: "#94a3b8", fontSize: 13 }}>Cho roster sync và overbook tiếp tục theo vote.</div>
              </div>
            </div>
            <div style={toggleRowStyle}>
              <input
                type="checkbox"
                checked={form.assumePmForHourUnder12}
                onChange={(event) => setForm((current) => ({ ...current, assumePmForHourUnder12: event.target.checked }))}
              />
              <div>
                <strong>Hiểu 5h30 là 17:30</strong>
                <div style={{ color: "#94a3b8", fontSize: 13 }}>Áp dụng khi option ghi giờ 1–11 mà không nói sáng/chiều.</div>
              </div>
            </div>
          </div>

          {!form.requireOrganizerApproval ? (
            <div style={{ marginTop: 12, padding: 12, borderRadius: 12, background: "rgba(245, 158, 11, 0.12)", color: "#fcd34d" }}>
              Đang tắt xác nhận: poll hợp lệ do trưởng/phó tạo có thể sinh session ngay sau khi classifier chấp nhận.
            </div>
          ) : null}

          <div style={{ ...gridStyle, marginTop: 16 }}>
            <label>
              <span style={{ display: "flex", gap: 6, alignItems: "center", marginBottom: 6, color: "#cbd5e1" }}>
                <Users size={15} /> Slot mỗi team
              </span>
              <input
                type="number"
                min={2}
                max={30}
                value={form.defaultTeamSize}
                onChange={(event) => setForm((current) => ({ ...current, defaultTeamSize: Number(event.target.value) }))}
                style={inputStyle}
              />
              <small style={{ color: "#64748b" }}>MVP cố định 3 team · capacity hiện tại {3 * Number(form.defaultTeamSize || 0)}.</small>
            </label>
            <label>
              <span style={{ display: "block", marginBottom: 6, color: "#cbd5e1" }}>Số set mặc định</span>
              <input
                type="number"
                min={1}
                max={20}
                value={form.defaultTotalSets}
                onChange={(event) => setForm((current) => ({ ...current, defaultTotalSets: Number(event.target.value) }))}
                style={inputStyle}
              />
            </label>
            <label>
              <span style={{ display: "flex", gap: 6, alignItems: "center", marginBottom: 6, color: "#cbd5e1" }}>
                <Clock3 size={15} /> Giờ mặc định
              </span>
              <input
                type="time"
                value={form.defaultStartTime}
                onChange={(event) => setForm((current) => ({ ...current, defaultStartTime: event.target.value }))}
                style={inputStyle}
              />
              <small style={{ color: "#64748b" }}>Dùng khi option chỉ ghi T4/T6/CN mà không có giờ.</small>
            </label>
            <label>
              <span style={{ display: "flex", gap: 6, alignItems: "center", marginBottom: 6, color: "#cbd5e1" }}>
                <MapPin size={15} /> Sân mặc định
              </span>
              <input
                value={form.defaultLocation}
                onChange={(event) => setForm((current) => ({ ...current, defaultLocation: event.target.value }))}
                placeholder="Ví dụ: Sân UTE"
                style={inputStyle}
              />
            </label>
          </div>

          <div style={{ marginTop: 16, display: "flex", gap: 12, alignItems: "center", flexWrap: "wrap" }}>
            <button
              type="button"
              disabled={busy}
              onClick={() => void saveSettings()}
              style={{ ...buttonStyle, background: "#22c55e", color: "#052e16" }}
            >
              <Save size={16} /> Lưu Auto Session
            </button>
            <div style={{ color: "#94a3b8", fontSize: 13 }}>
              <ShieldCheck size={14} style={{ verticalAlign: "-2px", marginRight: 5 }} />
              {selectedTracked.connectionDisplayName} · {selectedTracked.existingSessionCount} session đã liên kết · cập nhật {formatUpdatedAt(selectedTracked.updatedAt)}
            </div>
          </div>
        </>
      ) : null}

      {message ? (
        <div style={{ marginTop: 14, padding: "10px 12px", borderRadius: 10, background: "rgba(51, 65, 85, 0.75)", color: "#e2e8f0" }}>
          {message}
        </div>
      ) : null}
    </section>
  );
}
