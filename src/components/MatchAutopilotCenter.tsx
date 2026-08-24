import { useEffect, useMemo, useState } from "react";
import { Bot, CheckCircle2, Copy, RefreshCw, TriangleAlert } from "lucide-react";
import {
  ApiRequestError,
  apiFetch,
  type AdminSessionSummaryResponse,
  type PagedResponse,
  type SessionPlayerResponse,
  type SessionResponse,
} from "../api/dbClient";

type OverbookStatusLite = {
  enabled: boolean;
  capacity: number;
  effectiveSlotCount: number;
  excessSlotCount: number;
  needsConfirmation: boolean;
  lastError: string | null;
};

type LifecycleStage =
  | "NeedsSetup"
  | "Recruiting"
  | "ResolvingOverbook"
  | "AwaitingProfiles"
  | "ReadyForDraft"
  | "Drafting"
  | "Drafted"
  | "Cancelled";

type LifecycleCard = {
  id: string;
  name: string;
  stage: LifecycleStage;
  stageLabel: string;
  statusText: string;
  nextStep: string;
  startTime: string | null;
  effectiveSlots: number;
  capacity: number;
  missingProfiles: string[];
  needsWebsite: boolean;
  webTarget: "draft-workspace" | "auto-session-control" | "bot-overbook-control" | null;
  command: string | null;
};

const stageRank: Record<LifecycleStage, number> = {
  NeedsSetup: 0,
  ResolvingOverbook: 1,
  AwaitingProfiles: 2,
  Recruiting: 3,
  ReadyForDraft: 4,
  Drafting: 5,
  Drafted: 6,
  Cancelled: 7,
};

function formatStart(value: string | null) {
  if (!value) return "Chưa có giờ đấu";
  return new Intl.DateTimeFormat("vi-VN", {
    weekday: "short",
    day: "2-digit",
    month: "2-digit",
    hour: "2-digit",
    minute: "2-digit",
  }).format(new Date(value));
}

function classify(
  summary: AdminSessionSummaryResponse,
  session: SessionResponse,
  players: SessionPlayerResponse[],
  overbook: OverbookStatusLite | null,
): LifecycleCard {
  const presentPlayers = players.filter((player) => player.isPresent);
  const capacity = overbook?.capacity ?? Math.max(1, session.teamCount * session.teamSize);
  const effectiveSlots = overbook?.effectiveSlotCount ?? summary.playerCount;
  const missingProfiles = presentPlayers
    .filter((player) => player.gender === "Unknown")
    .map((player) => player.displayName)
    .filter(Boolean);

  const base = {
    id: session.id,
    name: session.name,
    startTime: session.startTime,
    effectiveSlots,
    capacity,
    missingProfiles,
  };

  if (session.status === "Cancelled") {
    return {
      ...base,
      stage: "Cancelled",
      stageLabel: "Đã hủy",
      statusText: "Kèo đã hủy, autopilot không còn việc phải làm.",
      nextStep: "Không cần thao tác.",
      needsWebsite: false,
      webTarget: null,
      command: null,
    };
  }

  if (session.status === "Finished") {
    return {
      ...base,
      stage: "Drafted",
      stageLabel: "Đã có team",
      statusText: "Draft đã hoàn tất. Bot có thể tiếp tục xử lý các thay đổi hậu draft trên Zalo.",
      nextStep: "Chỉ cần can thiệp nếu bot báo conflict hoặc cần rollback.",
      needsWebsite: false,
      webTarget: null,
      command: null,
    };
  }

  if (session.status === "Drafting") {
    return {
      ...base,
      stage: "Drafting",
      stageLabel: "Đang draft",
      statusText: "Draft đang chạy; không nên sửa roster/cấu hình giữa chừng.",
      nextStep: "Tiếp tục draft trên điện thoại hoặc Zalo theo flow hiện tại.",
      needsWebsite: false,
      webTarget: null,
      command: null,
    };
  }

  if (!session.zaloConnectionId || !session.zaloGroupId) {
    return {
      ...base,
      stage: "NeedsSetup",
      stageLabel: "Cần cấu hình một lần",
      statusText: "Trận chưa gắn Zalo connection/group nên bot chưa thể tự theo dõi poll và hội thoại.",
      nextStep: "Mở khu vực Auto Session/Zalo để liên kết group. Sau đó các trận sau có thể tự tạo từ poll.",
      needsWebsite: true,
      webTarget: "auto-session-control",
      command: null,
    };
  }

  if (!session.startTime) {
    return {
      ...base,
      stage: "NeedsSetup",
      stageLabel: "Thiếu giờ đấu",
      statusText: "Bot không thể tính cửa sổ reminder, guest và draft escalation khi chưa biết giờ bắt đầu.",
      nextStep: "Bổ sung giờ đấu một lần trên web hoặc để Auto Session đọc giờ từ poll.",
      needsWebsite: true,
      webTarget: "draft-workspace",
      command: null,
    };
  }

  if (!session.botEnabled) {
    return {
      ...base,
      stage: "NeedsSetup",
      stageLabel: "Bot đang tắt",
      statusText: "Session đã gắn Zalo nhưng bot đang tắt nên lifecycle không thể chạy tự động.",
      nextStep: "Bật bot cho session này nếu muốn dùng zero-web flow.",
      needsWebsite: true,
      webTarget: "bot-overbook-control",
      command: null,
    };
  }

  if (overbook?.needsConfirmation) {
    return {
      ...base,
      stage: "ResolvingOverbook",
      stageLabel: "Cần xác nhận dư slot",
      statusText: `Roster đang ${effectiveSlots}/${capacity}; hệ thống không đủ bằng chứng để tự chọn người vượt slot.`,
      nextStep: "Đây là exception thật: admin xác nhận target dư slot trên web rồi bot mới tiếp tục.",
      needsWebsite: true,
      webTarget: "bot-overbook-control",
      command: null,
    };
  }

  if (effectiveSlots > capacity) {
    const automatic = overbook?.enabled === true;
    return {
      ...base,
      stage: "ResolvingOverbook",
      stageLabel: automatic ? "Bot đang xử lý dư slot" : "Dư slot",
      statusText: `Hiện ${effectiveSlots}/${capacity}, dư ${effectiveSlots - capacity} slot.`,
      nextStep: automatic
        ? "Overbook reminder đang có thể tự xử lý theo state hiện tại; chưa cần mở web trừ khi bot báo cần confirmation."
        : "Overbook automation đang tắt; bật automation hoặc xử lý roster trên web.",
      needsWebsite: !automatic,
      webTarget: automatic ? null : "bot-overbook-control",
      command: null,
    };
  }

  if (effectiveSlots < capacity) {
    const missing = capacity - effectiveSlots;
    return {
      ...base,
      stage: "Recruiting",
      stageLabel: `Roster dưới mốc · còn ${missing}`,
      statusText: `Theo capacity chuẩn hiện có ${effectiveSlots}/${capacity} effective slot. Đây chưa phải lệnh bắt buộc phải tuyển thêm.`,
      nextStep: "Nếu trưởng/phó đã chọn `kiếm thêm`, KeepRecruiting tự sync/nhắc. Nếu đã chốt chơi roster hiện tại (ví dụ 15 vẫn đánh), quyết định Zalo đó vẫn là authoritative và client không cần mở web chỉ vì chưa đủ 18.",
      needsWebsite: false,
      webTarget: null,
      command: null,
    };
  }

  if (missingProfiles.length > 0) {
    return {
      ...base,
      stage: "AwaitingProfiles",
      stageLabel: `Thiếu hồ sơ · ${missingProfiles.length}`,
      statusText: `Đủ ${effectiveSlots}/${capacity} slot nhưng còn hồ sơ chưa xác nhận: ${missingProfiles.slice(0, 5).join(", ")}${missingProfiles.length > 5 ? "…" : ""}.`,
      nextStep: "Xử lý ngay trong Zalo bằng lệnh hỏi người thiếu hồ sơ; không cần mở form web.",
      needsWebsite: false,
      webTarget: null,
      command: `ai chưa cập nhật hồ sơ ${session.name}`,
    };
  }

  return {
    ...base,
    stage: "ReadyForDraft",
    stageLabel: "Có thể yêu cầu draft",
    statusText: `Roster đang ${effectiveSlots}/${capacity} và client không thấy người có giới tính Unknown. Backend DraftAutopilot vẫn là cổng readiness cuối cùng.`,
    nextStep: "Trưởng/phó có thể nói `draft đi` trên Zalo. Bot sẽ sync poll lần cuối, kiểm tra authoritative profile/slot state và đi qua confirmation gate trước khi mutation.",
    needsWebsite: false,
    webTarget: null,
    command: "draft đi",
  };
}

function scrollToTarget(target: LifecycleCard["webTarget"]) {
  if (!target) return;
  document.getElementById(target)?.scrollIntoView({ behavior: "smooth", block: "start" });
}

export function MatchAutopilotCenter() {
  const [token, setToken] = useState(() => localStorage.getItem("volleyDraftToken"));
  const [cards, setCards] = useState<LifecycleCard[]>([]);
  const [loading, setLoading] = useState(false);
  const [message, setMessage] = useState<string | null>(null);
  const [copied, setCopied] = useState<string | null>(null);

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
      setCards([]);
      return;
    }

    void load(token);
    const timer = window.setInterval(() => void load(token, true), 30_000);
    return () => window.clearInterval(timer);
  }, [token]);

  const stats = useMemo(() => {
    const needsWebsite = cards.filter((card) => card.needsWebsite).length;
    const done = cards.filter((card) => card.stage === "Drafted" || card.stage === "Cancelled").length;
    return {
      needsWebsite,
      noWebsite: Math.max(0, cards.length - needsWebsite - done),
      done,
    };
  }, [cards]);

  if (!token) return null;

  async function load(authToken = token, silent = false) {
    if (!authToken) return;
    if (!silent) setLoading(true);
    try {
      const page = await apiFetch<PagedResponse<AdminSessionSummaryResponse>>(
        "/sessions?page=1&pageSize=10",
        { token: authToken },
      );
      const active = page.items.filter((item) => item.status !== "Cancelled" || item.playerCount > 0);
      const nextCards = await Promise.all(
        active.map(async (summary) => {
          const [session, players] = await Promise.all([
            apiFetch<SessionResponse>(`/sessions/${summary.id}`, { token: authToken }),
            apiFetch<SessionPlayerResponse[]>(`/sessions/${summary.id}/players`, { token: authToken }),
          ]);

          let overbook: OverbookStatusLite | null = null;
          if (session.zaloConnectionId && session.zaloGroupId) {
            try {
              overbook = await apiFetch<OverbookStatusLite>(`/sessions/${summary.id}/zalo-overbook`, {
                token: authToken,
              });
            } catch {
              // A missing/temporarily unreadable poll must not make the whole control center unusable.
              // Session + roster data still gives the operator a useful lifecycle view.
            }
          }
          return classify(summary, session, players, overbook);
        }),
      );

      nextCards.sort((left, right) => {
        if (left.needsWebsite !== right.needsWebsite) return left.needsWebsite ? -1 : 1;
        const leftTime = left.startTime ? new Date(left.startTime).getTime() : Number.MAX_SAFE_INTEGER;
        const rightTime = right.startTime ? new Date(right.startTime).getTime() : Number.MAX_SAFE_INTEGER;
        if (leftTime !== rightTime) return leftTime - rightTime;
        return stageRank[left.stage] - stageRank[right.stage];
      });
      setCards(nextCards);
      setMessage(null);
    } catch (error) {
      if (!silent) {
        setMessage(error instanceof ApiRequestError ? error.message : "Không tải được Match Autopilot Center.");
      }
    } finally {
      if (!silent) setLoading(false);
    }
  }

  async function copyCommand(card: LifecycleCard) {
    if (!card.command) return;
    await navigator.clipboard.writeText(card.command);
    setCopied(card.id);
    window.setTimeout(() => setCopied((current) => current === card.id ? null : current), 1600);
  }

  return (
    <section
      id="autopilot-center"
      style={{
        margin: "24px auto 0",
        maxWidth: 1180,
        padding: 20,
        borderRadius: 20,
        border: "1px solid rgba(34,197,94,.2)",
        background: "linear-gradient(145deg, rgba(8,47,73,.92), rgba(15,23,42,.94))",
        color: "#e2e8f0",
      }}
    >
      <div style={{ display: "flex", justifyContent: "space-between", gap: 14, alignItems: "flex-start", flexWrap: "wrap" }}>
        <div style={{ maxWidth: 760 }}>
          <div style={{ display: "flex", alignItems: "center", gap: 9 }}>
            <Bot size={23} />
            <h2 style={{ margin: 0 }}>Match Autopilot Center</h2>
          </div>
          <p style={{ margin: "8px 0 0", color: "#cbd5e1", lineHeight: 1.55 }}>
            Mục tiêu: client làm việc trên Zalo trước. Website chỉ sáng cảnh báo khi có exception mà bot không nên tự quyết.
            Trạng thái tự làm mới mỗi 30 giây.
          </p>
        </div>
        <button
          type="button"
          disabled={loading}
          onClick={() => void load()}
          style={{
            display: "inline-flex",
            gap: 7,
            alignItems: "center",
            border: 0,
            borderRadius: 10,
            padding: "10px 14px",
            background: "#334155",
            color: "#f8fafc",
            fontWeight: 800,
            cursor: loading ? "wait" : "pointer",
          }}
        >
          <RefreshCw size={16} /> {loading ? "Đang kiểm tra…" : "Kiểm tra ngay"}
        </button>
      </div>

      <div style={{ display: "grid", gridTemplateColumns: "repeat(auto-fit, minmax(190px, 1fr))", gap: 10, marginTop: 16 }}>
        <Stat label="Bot/Zalo xử lý tiếp" value={stats.noWebsite} helper="Không cần mở thêm màn web" />
        <Stat label="Cần admin trên web" value={stats.needsWebsite} helper="Exception thật sự cần người quyết" attention={stats.needsWebsite > 0} />
        <Stat label="Đã xong / đã hủy" value={stats.done} helper="Không còn việc chuẩn bị" />
      </div>

      {cards.length === 0 && !loading ? (
        <div style={{ marginTop: 16, padding: 16, borderRadius: 14, background: "rgba(15,23,42,.58)", color: "#94a3b8" }}>
          Chưa có session để theo dõi.
        </div>
      ) : null}

      <div style={{ display: "grid", gap: 12, marginTop: 16 }}>
        {cards.map((card) => (
          <article
            key={card.id}
            style={{
              padding: 16,
              borderRadius: 16,
              border: card.needsWebsite ? "1px solid rgba(251,191,36,.38)" : "1px solid rgba(148,163,184,.2)",
              background: card.needsWebsite ? "rgba(120,53,15,.16)" : "rgba(15,23,42,.58)",
            }}
          >
            <div style={{ display: "flex", justifyContent: "space-between", gap: 12, flexWrap: "wrap" }}>
              <div>
                <div style={{ display: "flex", gap: 8, alignItems: "center", flexWrap: "wrap" }}>
                  {card.needsWebsite ? <TriangleAlert size={18} /> : <CheckCircle2 size={18} />}
                  <strong style={{ fontSize: 17 }}>{card.name}</strong>
                  <span style={{ padding: "3px 8px", borderRadius: 999, background: "rgba(148,163,184,.15)", fontSize: 12, fontWeight: 800 }}>
                    {card.stageLabel}
                  </span>
                </div>
                <div style={{ marginTop: 6, color: "#94a3b8", fontSize: 13 }}>
                  {formatStart(card.startTime)} · {card.effectiveSlots}/{card.capacity} effective slot
                </div>
              </div>
              <strong style={{ color: card.needsWebsite ? "#fbbf24" : "#86efac", fontSize: 13 }}>
                {card.needsWebsite ? "CẦN WEBSITE" : "KHÔNG CẦN WEBSITE"}
              </strong>
            </div>

            <p style={{ margin: "12px 0 0", lineHeight: 1.5 }}>{card.statusText}</p>
            <p style={{ margin: "7px 0 0", color: "#cbd5e1", lineHeight: 1.5 }}>
              <strong>Bước tiếp:</strong> {card.nextStep}
            </p>

            <div style={{ display: "flex", gap: 8, marginTop: 13, flexWrap: "wrap" }}>
              {card.command ? (
                <button
                  type="button"
                  onClick={() => void copyCommand(card)}
                  style={{
                    display: "inline-flex",
                    alignItems: "center",
                    gap: 7,
                    border: "1px solid rgba(148,163,184,.28)",
                    borderRadius: 10,
                    padding: "9px 12px",
                    background: "rgba(15,23,42,.72)",
                    color: "#f8fafc",
                    fontWeight: 750,
                    cursor: "pointer",
                  }}
                >
                  <Copy size={15} /> {copied === card.id ? "Đã copy" : `Copy: ${card.command}`}
                </button>
              ) : null}
              {card.needsWebsite && card.webTarget ? (
                <button
                  type="button"
                  onClick={() => scrollToTarget(card.webTarget)}
                  style={{
                    border: 0,
                    borderRadius: 10,
                    padding: "9px 12px",
                    background: "#f59e0b",
                    color: "#1c1917",
                    fontWeight: 900,
                    cursor: "pointer",
                  }}
                >
                  Đi tới đúng chỗ xử lý
                </button>
              ) : null}
            </div>
          </article>
        ))}
      </div>

      {message ? (
        <div style={{ marginTop: 12, padding: "10px 12px", borderRadius: 10, background: "rgba(239,68,68,.1)", color: "#fecaca" }}>
          {message}
        </div>
      ) : null}
    </section>
  );
}

function Stat({ label, value, helper, attention = false }: { label: string; value: number; helper: string; attention?: boolean }) {
  return (
    <div style={{ padding: 13, borderRadius: 13, background: attention ? "rgba(245,158,11,.13)" : "rgba(15,23,42,.55)", border: "1px solid rgba(148,163,184,.16)" }}>
      <div style={{ fontSize: 25, fontWeight: 900 }}>{value}</div>
      <div style={{ marginTop: 2, fontWeight: 800 }}>{label}</div>
      <div style={{ marginTop: 4, color: "#94a3b8", fontSize: 12 }}>{helper}</div>
    </div>
  );
}
