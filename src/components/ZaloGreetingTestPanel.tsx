import { useEffect, useMemo, useState } from "react";
import { ImagePlus, MoonStar, RefreshCw, Send, SunMedium } from "lucide-react";
import {
  ApiRequestError,
  apiFetch,
  type ZaloConnectionResponse,
  type ZaloGroupResponse,
} from "../api/dbClient";

type GreetingKind = "Morning" | "Night";

type GreetingPreview = {
  assetId: string;
  kind: GreetingKind;
  groupId: string;
  groupName: string;
  groupAvatarUrl: string | null;
  message: string;
  testSendMessage: string;
  imageUrl: string;
  backgroundId: number;
  mood: string;
  affectsProductionSchedule: false;
};

type GreetingSendResponse = {
  sent: boolean;
  mock: boolean;
  messageId: string | null;
  groupId: string;
  groupName: string;
  kind: GreetingKind;
  imageUrl: string;
  message: string;
  affectsProductionSchedule: false;
};

export function ZaloGreetingTestPanel() {
  const [token, setToken] = useState(() => localStorage.getItem("volleyDraftToken"));
  const [connections, setConnections] = useState<ZaloConnectionResponse[]>([]);
  const [groups, setGroups] = useState<ZaloGroupResponse[]>([]);
  const [connectionId, setConnectionId] = useState("");
  const [groupId, setGroupId] = useState("");
  const [kind, setKind] = useState<GreetingKind>("Night");
  const [backgroundId, setBackgroundId] = useState(0);
  const [preview, setPreview] = useState<GreetingPreview | null>(null);
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
      setConnections([]);
      setGroups([]);
      setConnectionId("");
      setGroupId("");
      setPreview(null);
      return;
    }
    void loadConnections(token);
  }, [token]);

  useEffect(() => {
    if (!token || !connectionId) {
      setGroups([]);
      setGroupId("");
      setPreview(null);
      return;
    }
    void loadGroups(token, connectionId);
  }, [token, connectionId]);

  useEffect(() => {
    setPreview(null);
    setMessage(null);
  }, [groupId, kind, backgroundId]);

  const selectedGroup = useMemo(
    () => groups.find((item) => item.id === groupId) ?? null,
    [groups, groupId],
  );

  if (!token) return null;

  async function loadConnections(authToken = token) {
    if (!authToken) return;
    setBusy(true);
    try {
      const next = await apiFetch<ZaloConnectionResponse[]>("/zalo/connections", { token: authToken });
      const connected = next.filter((item) => item.status === "Connected");
      setConnections(connected);
      setConnectionId((current) =>
        connected.some((item) => item.id === current) ? current : connected[0]?.id ?? "",
      );
      setMessage(null);
    } catch (error) {
      setMessage(error instanceof ApiRequestError ? error.message : "Không tải được kết nối Zalo.");
    } finally {
      setBusy(false);
    }
  }

  async function loadGroups(authToken: string, nextConnectionId: string) {
    setLoadingGroups(true);
    try {
      const next = await apiFetch<ZaloGroupResponse[]>(
        `/zalo/connections/${encodeURIComponent(nextConnectionId)}/groups`,
        { token: authToken },
      );
      setGroups(next);
      setGroupId((current) => (next.some((item) => item.id === current) ? current : next[0]?.id ?? ""));
      setMessage(null);
    } catch (error) {
      setGroups([]);
      setGroupId("");
      setMessage(error instanceof ApiRequestError ? error.message : "Không đọc được danh sách group Zalo.");
    } finally {
      setLoadingGroups(false);
    }
  }

  async function createPreview() {
    if (!token || !connectionId || !groupId) return;
    setBusy(true);
    setPreview(null);
    try {
      const next = await apiFetch<GreetingPreview>("/zalo/greeting-tests/preview", {
        method: "POST",
        token,
        body: {
          connectionId,
          groupId,
          kind,
          backgroundId: backgroundId === 0 ? null : backgroundId,
        },
      });
      setPreview(next);
      setMessage(
        `Đã tạo preview ${next.kind} · background ${next.backgroundId} · ${next.mood}. Chưa gửi gì vào Zalo.`,
      );
    } catch (error) {
      setMessage(error instanceof ApiRequestError ? error.message : "Không tạo được greeting preview.");
    } finally {
      setBusy(false);
    }
  }

  async function sendPreview() {
    if (!token || !connectionId || !groupId || !preview) return;

    const confirmed = window.confirm(
      `Gửi THẬT ${preview.kind} greeting test vào group “${preview.groupName}”?\n\n` +
        "Ảnh dùng đúng production renderer. Tin nhắn sẽ có nhãn 🧪 TEST để không bị scheduler tính là lời chúc thật.",
    );
    if (!confirmed) return;

    setBusy(true);
    try {
      const result = await apiFetch<GreetingSendResponse>("/zalo/greeting-tests/send", {
        method: "POST",
        token,
        body: {
          connectionId,
          groupId,
          kind: preview.kind,
          assetId: preview.assetId,
          message: preview.message,
        },
      });
      setMessage(
        result.mock
          ? `Bridge đang mock; request ${result.kind} đã chạy nhưng chưa gửi provider thật.`
          : result.sent
            ? `Đã gửi ${result.kind} test vào ${result.groupName}. Production schedule/rotation không bị đánh dấu đã gửi.`
            : `Bridge không xác nhận đã gửi ${result.kind} test.`,
      );
    } catch (error) {
      setMessage(error instanceof ApiRequestError ? error.message : "Gửi greeting test thất bại.");
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
    background: "rgba(15, 23, 42, 0.82)",
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

  return (
    <section style={cardStyle}>
      <div style={{ display: "flex", justifyContent: "space-between", gap: 16, alignItems: "flex-start", flexWrap: "wrap" }}>
        <div>
          <div style={{ display: "flex", alignItems: "center", gap: 9 }}>
            {kind === "Morning" ? <SunMedium size={22} /> : <MoonStar size={22} />}
            <h2 style={{ margin: 0 }}>Test lời chúc Zalo thật</h2>
          </div>
          <p style={{ margin: "8px 0 0", color: "#94a3b8", maxWidth: 780 }}>
            Tạo bằng đúng renderer/copy production và tên group Zalo live. Preview dùng asset riêng, không đánh dấu đã chúc hôm nay và không tiêu rotation Morning/Night thật.
          </p>
        </div>
        <button
          type="button"
          disabled={busy}
          onClick={() => void loadConnections()}
          style={{ ...buttonStyle, background: "#334155", color: "#f8fafc" }}
        >
          <RefreshCw size={16} /> Làm mới
        </button>
      </div>

      <div style={{ ...gridStyle, marginTop: 18 }}>
        <label>
          <span style={{ display: "block", marginBottom: 6, color: "#cbd5e1" }}>Tài khoản Zalo</span>
          <select value={connectionId} onChange={(event) => setConnectionId(event.target.value)} style={inputStyle}>
            {connections.length === 0 ? <option value="">Chưa có kết nối Connected</option> : null}
            {connections.map((item) => (
              <option key={item.id} value={item.id}>{item.displayName} · {item.accountZaloId}</option>
            ))}
          </select>
        </label>

        <label>
          <span style={{ display: "block", marginBottom: 6, color: "#cbd5e1" }}>Group Zalo thật</span>
          <select
            value={groupId}
            onChange={(event) => setGroupId(event.target.value)}
            style={inputStyle}
            disabled={loadingGroups || groups.length === 0}
          >
            {loadingGroups ? <option value="">Đang tải group…</option> : null}
            {!loadingGroups && groups.length === 0 ? <option value="">Không có group</option> : null}
            {groups.map((item) => (
              <option key={item.id} value={item.id}>{item.name} · {item.totalMembers} thành viên</option>
            ))}
          </select>
        </label>

        <label>
          <span style={{ display: "block", marginBottom: 6, color: "#cbd5e1" }}>Loại lời chúc</span>
          <select value={kind} onChange={(event) => setKind(event.target.value as GreetingKind)} style={inputStyle}>
            <option value="Morning">☀️ Morning</option>
            <option value="Night">🌙 Night</option>
          </select>
        </label>

        <label>
          <span style={{ display: "block", marginBottom: 6, color: "#cbd5e1" }}>Background</span>
          <select value={backgroundId} onChange={(event) => setBackgroundId(Number(event.target.value))} style={inputStyle}>
            <option value={0}>Ngẫu nhiên như production</option>
            {[1, 2, 3, 4, 5].map((id) => <option key={id} value={id}>Background {id}</option>)}
          </select>
        </label>
      </div>

      {selectedGroup ? (
        <div style={{ marginTop: 14, display: "flex", alignItems: "center", gap: 10, color: "#cbd5e1" }}>
          {selectedGroup.avatarUrl ? (
            <img
              src={selectedGroup.avatarUrl}
              alt=""
              style={{ width: 38, height: 38, borderRadius: "50%", objectFit: "cover" }}
            />
          ) : null}
          <span>Đang test với <strong>{selectedGroup.name}</strong></span>
        </div>
      ) : null}

      <div style={{ display: "flex", gap: 10, marginTop: 16, flexWrap: "wrap" }}>
        <button
          type="button"
          disabled={busy || !connectionId || !groupId}
          onClick={() => void createPreview()}
          style={{ ...buttonStyle, background: "#7c3aed", color: "white" }}
        >
          <ImagePlus size={16} /> {preview ? "Tạo bản khác" : "Tạo preview"}
        </button>
        <button
          type="button"
          disabled={busy || !preview}
          onClick={() => void sendPreview()}
          style={{ ...buttonStyle, background: preview ? "#16a34a" : "#334155", color: "white" }}
        >
          <Send size={16} /> Gửi preview này vào Zalo thật
        </button>
      </div>

      {preview ? (
        <div style={{ marginTop: 18, display: "grid", gridTemplateColumns: "minmax(280px, 520px) 1fr", gap: 18, alignItems: "start" }}>
          <img
            src={preview.imageUrl}
            alt={`${preview.kind} greeting preview`}
            style={{ width: "100%", borderRadius: 16, display: "block", boxShadow: "0 16px 45px rgba(0,0,0,0.28)" }}
          />
          <div style={{ padding: 16, borderRadius: 14, background: "rgba(30, 41, 59, 0.58)" }}>
            <div style={{ fontSize: 13, color: "#a78bfa", fontWeight: 800, letterSpacing: ".04em" }}>
              {preview.kind.toUpperCase()} · BG {preview.backgroundId} · {preview.mood}
            </div>
            <p style={{ margin: "12px 0 0", fontSize: 18, lineHeight: 1.55 }}>{preview.message}</p>
            <div style={{ marginTop: 14, padding: 12, borderRadius: 10, background: "rgba(2, 6, 23, 0.52)" }}>
              <div style={{ fontSize: 12, color: "#fbbf24", fontWeight: 800 }}>TIN NHẮN KHI GỬI TEST THẬT</div>
              <div style={{ marginTop: 5, color: "#cbd5e1", lineHeight: 1.5 }}>{preview.testSendMessage}</div>
            </div>
            <p style={{ margin: "14px 0 0", color: "#86efac", fontSize: 14 }}>
              ✓ Card/copy đúng production. Prefix 🧪 TEST chỉ bảo vệ scheduler khỏi tính test là lời chúc thật.
            </p>
          </div>
        </div>
      ) : null}

      {message ? (
        <p style={{ margin: "14px 0 0", color: message.startsWith("Đã") ? "#86efac" : "#fbbf24" }}>{message}</p>
      ) : null}
    </section>
  );
}
