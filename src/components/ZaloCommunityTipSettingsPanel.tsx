import { useEffect, useMemo, useState } from "react";
import { Lightbulb, RefreshCw, Save } from "lucide-react";
import { ApiRequestError, apiFetch } from "../api/dbClient";

type AutoSessionGroup = {
  id: string;
  groupName: string;
  autoSessionEnabled: boolean;
  requireOrganizerApproval: boolean;
  defaultTeamSize: number;
  defaultTotalSets: number;
  defaultStartTime: string;
  assumePmForHourUnder12: boolean;
  defaultLocation: string | null;
  botEnabledForCreatedSessions: boolean;
  communityTipDailyCount: number;
};

export function ZaloCommunityTipSettingsPanel() {
  const [token, setToken] = useState(() => localStorage.getItem("volleyDraftToken"));
  const [groups, setGroups] = useState<AutoSessionGroup[]>([]);
  const [selectedId, setSelectedId] = useState("");
  const [dailyCount, setDailyCount] = useState(1);
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
    () => groups.find((item) => item.id === selectedId) ?? null,
    [groups, selectedId],
  );

  useEffect(() => {
    if (!selected) return;
    setDailyCount(Math.min(5, Math.max(1, selected.communityTipDailyCount || 1)));
  }, [selected]);

  if (!token) return null;

  async function load(authToken = token) {
    if (!authToken) return;
    setBusy(true);
    try {
      const next = await apiFetch<AutoSessionGroup[]>("/zalo/auto-session-groups", { token: authToken });
      setGroups(next);
      setSelectedId((current) => next.some((item) => item.id === current) ? current : next[0]?.id ?? "");
      setMessage(null);
    } catch (error) {
      setMessage(error instanceof ApiRequestError ? error.message : "Không tải được cấu hình STT.");
    } finally {
      setBusy(false);
    }
  }

  async function save() {
    if (!token || !selected) return;
    setBusy(true);
    try {
      const updated = await apiFetch<AutoSessionGroup>(`/zalo/auto-session-groups/${selected.id}`, {
        method: "PUT",
        token,
        body: {
          autoSessionEnabled: selected.autoSessionEnabled,
          requireOrganizerApproval: selected.requireOrganizerApproval,
          defaultTeamSize: selected.defaultTeamSize,
          defaultTotalSets: selected.defaultTotalSets,
          defaultStartTime: selected.defaultStartTime,
          assumePmForHourUnder12: selected.assumePmForHourUnder12,
          defaultLocation: selected.defaultLocation,
          botEnabledForCreatedSessions: selected.botEnabledForCreatedSessions,
          communityTipDailyCount: Number(dailyCount),
        },
      });
      setGroups((current) => current.map((item) => item.id === updated.id ? updated : item));
      setDailyCount(updated.communityTipDailyCount);
      setMessage(`Đã đặt ${updated.communityTipDailyCount} STT/ngày cho ${updated.groupName}.`);
    } catch (error) {
      setMessage(error instanceof ApiRequestError ? error.message : "Không lưu được số lần STT mỗi ngày.");
    } finally {
      setBusy(false);
    }
  }

  const cardStyle = {
    margin: "24px auto 0",
    maxWidth: 1180,
    padding: 20,
    borderRadius: 18,
    border: "1px solid rgba(168, 85, 247, 0.28)",
    background: "rgba(15, 23, 42, 0.76)",
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
    padding: "10px 14px",
    cursor: busy ? "wait" : "pointer",
    fontWeight: 700,
  } as const;

  return (
    <section style={cardStyle}>
      <div style={{ display: "flex", justifyContent: "space-between", gap: 14, alignItems: "flex-start", flexWrap: "wrap" }}>
        <div>
          <div style={{ display: "flex", gap: 8, alignItems: "center" }}>
            <Lightbulb size={21} />
            <h2 style={{ margin: 0 }}>STT · Community Tips</h2>
          </div>
          <p style={{ margin: "8px 0 0", color: "#94a3b8", maxWidth: 780 }}>
            Số lần bot chủ động nhắn mỗi ngày về share slot, muốn chơi chung và spotlight thành viên theo dữ liệu tham gia thật. Bot không tag người được nhắc và không tự thay đổi roster/team.
          </p>
        </div>
        <button
          type="button"
          disabled={busy}
          onClick={() => void load()}
          style={{ ...buttonStyle, background: "#334155", color: "#f8fafc" }}
        >
          <RefreshCw size={16} /> Làm mới
        </button>
      </div>

      <div style={{ display: "grid", gridTemplateColumns: "repeat(auto-fit, minmax(220px, 1fr))", gap: 12, marginTop: 18 }}>
        <label>
          <span style={{ display: "block", marginBottom: 6, color: "#cbd5e1" }}>Group Zalo</span>
          <select value={selectedId} onChange={(event) => setSelectedId(event.target.value)} style={inputStyle}>
            {groups.length === 0 ? <option value="">Chưa có group cấu hình</option> : null}
            {groups.map((item) => <option key={item.id} value={item.id}>{item.groupName}</option>)}
          </select>
        </label>

        <label>
          <span style={{ display: "block", marginBottom: 6, color: "#cbd5e1" }}>Số STT mỗi ngày</span>
          <select
            value={dailyCount}
            onChange={(event) => setDailyCount(Number(event.target.value))}
            style={inputStyle}
            disabled={!selected}
          >
            {[1, 2, 3, 4, 5].map((count) => (
              <option key={count} value={count}>{count} lần/ngày</option>
            ))}
          </select>
        </label>

        <div style={{ display: "flex", alignItems: "end" }}>
          <button
            type="button"
            disabled={busy || !selected}
            onClick={() => void save()}
            style={{ ...buttonStyle, width: "100%", background: "#7c3aed", color: "white" }}
          >
            <Save size={16} /> Lưu STT
          </button>
        </div>
      </div>

      <p style={{ margin: "12px 0 0", color: "#a78bfa", fontSize: 13 }}>
        Zalo cũng đổi được: trưởng/phó group tag bot rồi nhắn <strong>stt 1</strong> đến <strong>stt 5</strong>.
      </p>
      {message ? <p style={{ margin: "10px 0 0", color: "#86efac" }}>{message}</p> : null}
    </section>
  );
}
