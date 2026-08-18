import { useEffect, useMemo, useState } from "react";
import { Activity, RefreshCw } from "lucide-react";
import { ApiRequestError, apiFetch } from "../api/dbClient";
import {
  ZaloAutoSessionActivityView,
  type AutoSessionActivity,
} from "./ZaloAutoSessionActivityView";

type GroupWithActivity = {
  id: string;
  groupName: string;
  autoSessionEnabled: boolean;
  activity: AutoSessionActivity | null;
};

export function ZaloAutoSessionAuditPanel() {
  const [token, setToken] = useState(() => localStorage.getItem("volleyDraftToken"));
  const [groups, setGroups] = useState<GroupWithActivity[]>([]);
  const [selectedId, setSelectedId] = useState("");
  const [loading, setLoading] = useState(false);
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
    () => groups.find((item) => item.id === selectedId) ?? groups[0] ?? null,
    [groups, selectedId],
  );

  if (!token) return null;

  async function load(authToken = token) {
    if (!authToken) return;
    setLoading(true);
    try {
      const next = await apiFetch<GroupWithActivity[]>("/zalo/auto-session-groups", { token: authToken });
      setGroups(next);
      setSelectedId((current) => next.some((item) => item.id === current) ? current : next[0]?.id ?? "");
      setMessage(null);
    } catch (error) {
      setMessage(error instanceof ApiRequestError ? error.message : "Không tải được lịch sử Auto Session.");
    } finally {
      setLoading(false);
    }
  }

  return (
    <section
      style={{
        margin: "24px auto 0",
        maxWidth: 1180,
        padding: 20,
        borderRadius: 18,
        border: "1px solid rgba(59,130,246,.22)",
        background: "rgba(15,23,42,.76)",
        color: "#e2e8f0",
      }}
    >
      <div style={{ display: "flex", justifyContent: "space-between", gap: 14, alignItems: "flex-start", flexWrap: "wrap" }}>
        <div>
          <div style={{ display: "flex", gap: 8, alignItems: "center" }}>
            <Activity size={21} />
            <h2 style={{ margin: 0 }}>Auto Session Audit</h2>
          </div>
          <p style={{ color: "#94a3b8", margin: "7px 0 0" }}>
            Theo dõi poll → classifier → xác nhận → session → roster sync. Màn này chỉ đọc dữ liệu, không tạo hoặc huỷ trận.
          </p>
        </div>
        <button
          type="button"
          disabled={loading}
          onClick={() => void load()}
          style={{
            display: "inline-flex",
            alignItems: "center",
            gap: 7,
            border: 0,
            borderRadius: 10,
            padding: "10px 14px",
            background: "#334155",
            color: "#f8fafc",
            fontWeight: 700,
            cursor: loading ? "wait" : "pointer",
          }}
        >
          <RefreshCw size={16} /> {loading ? "Đang tải…" : "Làm mới audit"}
        </button>
      </div>

      <label style={{ display: "block", marginTop: 16 }}>
        <span style={{ display: "block", marginBottom: 6, color: "#cbd5e1" }}>Group</span>
        <select
          value={selected?.id ?? ""}
          onChange={(event) => setSelectedId(event.target.value)}
          style={{
            width: "100%",
            padding: "10px 12px",
            borderRadius: 10,
            border: "1px solid rgba(148,163,184,.35)",
            background: "rgba(15,23,42,.9)",
            color: "#f8fafc",
          }}
        >
          {groups.length === 0 ? <option value="">Chưa có tracked group</option> : null}
          {groups.map((item) => (
            <option key={item.id} value={item.id}>
              {item.autoSessionEnabled ? "🟢" : "⚪"} {item.groupName}
            </option>
          ))}
        </select>
      </label>

      <ZaloAutoSessionActivityView activity={selected?.activity} />

      {message ? (
        <div style={{ marginTop: 12, padding: "10px 12px", borderRadius: 10, background: "rgba(239,68,68,.1)", color: "#fca5a5" }}>
          {message}
        </div>
      ) : null}
    </section>
  );
}
