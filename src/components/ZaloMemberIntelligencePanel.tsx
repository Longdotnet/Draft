import { useEffect, useMemo, useState } from "react";
import { Activity, AlertTriangle, Database, RefreshCw, Users } from "lucide-react";
import {
  ApiRequestError,
  apiFetch,
  type SessionResponse,
  type ZaloActivityBackfillStage,
  type ZaloActivityBackfillStatusResponse,
  type ZaloEngagementStatus,
  type ZaloMemberActivityPageResponse,
} from "../api/dbClient";
import { Badge } from "./ui";

const FILTERS = [
  { value: "no-vote|30 ngày", label: "Chưa vote 30 ngày" },
  { value: "no-vote|60 ngày", label: "Chưa vote 60 ngày" },
  { value: "no-vote|90 ngày", label: "Chưa vote 90 ngày" },
  { value: "no-vote|4 tháng", label: "Chưa vote 4 tháng" },
  { value: "no-message|30 ngày", label: "Chưa nhắn 30 ngày" },
  { value: "no-message|60 ngày", label: "Chưa nhắn 60 ngày" },
  { value: "no-message|90 ngày", label: "Chưa nhắn 90 ngày" },
  { value: "no-message|4 tháng", label: "Chưa nhắn 4 tháng" },
  { value: "at-risk|90 ngày", label: "Có dấu hiệu giảm hoạt động" },
] as const;

function formatDate(value: string | null, includeTime = false) {
  if (!value) return "Chưa ghi nhận";
  return new Intl.DateTimeFormat("vi-VN", {
    day: "2-digit",
    month: "2-digit",
    year: "numeric",
    ...(includeTime ? { hour: "2-digit", minute: "2-digit" } : {}),
  }).format(new Date(value));
}

function formatStage(stage: ZaloActivityBackfillStage) {
  const labels: Record<ZaloActivityBackfillStage, string> = {
    Queued: "Đang chờ",
    SyncingMembers: "Đồng bộ thành viên",
    ScanningBoard: "Quét board",
    SyncingPollDetails: "Đồng bộ poll",
    ProbingMessageHistory: "Kiểm tra lịch sử chat",
    ImportingMessages: "Nhập tin nhắn",
    RebuildingMetrics: "Tính chỉ số",
    Completed: "Hoàn tất",
  };
  return labels[stage];
}

function statusLabel(status: ZaloEngagementStatus) {
  const labels: Record<ZaloEngagementStatus, string> = {
    New: "Thành viên mới",
    Active: "Tích cực",
    Regular: "Đều đặn",
    Occasional: "Thỉnh thoảng",
    AtRisk: "Đang giảm",
    Inactive: "Ít hoạt động",
    InsufficientData: "Chưa đủ dữ liệu",
  };
  return labels[status];
}

function statusTone(status: ZaloEngagementStatus): "neutral" | "orange" | "violet" | "good" {
  if (status === "Inactive" || status === "AtRisk") return "orange";
  if (status === "Active" || status === "Regular") return "good";
  if (status === "New") return "violet";
  return "neutral";
}

export function ZaloMemberIntelligencePanel({
  token,
  session,
}: {
  token: string;
  session: SessionResponse;
}) {
  const [sync, setSync] = useState<ZaloActivityBackfillStatusResponse | null>(null);
  const [activityPage, setActivityPage] = useState<ZaloMemberActivityPageResponse | null>(null);
  const [filterValue, setFilterValue] = useState<(typeof FILTERS)[number]["value"]>("no-vote|90 ngày");
  const [page, setPage] = useState(1);
  const [isSyncing, setIsSyncing] = useState(false);
  const [isLoading, setIsLoading] = useState(false);
  const [error, setError] = useState<string | null>(null);
  const [filter, period] = useMemo(() => filterValue.split("|"), [filterValue]);

  useEffect(() => {
    setPage(1);
  }, [filterValue, session.id]);

  useEffect(() => {
    if (!session.zaloGroupId) {
      setSync(null);
      setActivityPage(null);
      return;
    }
    void loadStatus();
  }, [token, session.id, session.zaloGroupId]);

  useEffect(() => {
    if (!session.zaloGroupId) return;
    void loadMembers(page);
  }, [token, session.id, session.zaloGroupId, filter, period, page]);

  useEffect(() => {
    if (!sync || !["Queued", "Running", "FailedRetryable"].includes(sync.status)) return;
    const timer = window.setInterval(() => void loadStatus(), 4000);
    return () => window.clearInterval(timer);
  }, [sync?.status, session.id]);

  async function loadStatus() {
    try {
      const result = await apiFetch<ZaloActivityBackfillStatusResponse>(
        `/sessions/${session.id}/member-intelligence/sync`,
        { token },
      );
      setSync(result);
      if (result.status === "Completed" || result.status === "CompletedWithLimitations") {
        void loadMembers(page);
      }
    } catch (requestError) {
      if (requestError instanceof ApiRequestError && requestError.status === 404) {
        setSync(null);
        return;
      }
      setError(requestError instanceof Error ? requestError.message : "Không tải được trạng thái đồng bộ.");
    }
  }

  async function startSync() {
    setIsSyncing(true);
    setError(null);
    try {
      const result = await apiFetch<ZaloActivityBackfillStatusResponse>(
        `/sessions/${session.id}/member-intelligence/sync`,
        {
          method: "POST",
          token,
          body: { full: true },
        },
      );
      setSync(result);
    } catch (requestError) {
      setError(requestError instanceof Error ? requestError.message : "Không thể bắt đầu đồng bộ.");
    } finally {
      setIsSyncing(false);
    }
  }

  async function loadMembers(targetPage: number) {
    setIsLoading(true);
    setError(null);
    try {
      const query = new URLSearchParams({
        filter,
        period,
        page: String(targetPage),
        pageSize: "10",
      });
      const result = await apiFetch<ZaloMemberActivityPageResponse>(
        `/sessions/${session.id}/member-intelligence/members?${query.toString()}`,
        { token },
      );
      setActivityPage(result);
    } catch (requestError) {
      setActivityPage(null);
      setError(requestError instanceof Error ? requestError.message : "Không tải được dữ liệu thành viên.");
    } finally {
      setIsLoading(false);
    }
  }

  if (!session.zaloGroupId) {
    return (
      <div className="zalo-step member-intelligence-panel">
        <div className="zalo-step-heading">
          <span><Activity size={16} aria-hidden="true" /></span>
          <strong>Member Intelligence</strong>
        </div>
        <p className="muted">Liên kết nhóm Zalo trước để đồng bộ thành viên, poll và hoạt động.</p>
      </div>
    );
  }

  return (
    <div className="zalo-step member-intelligence-panel">
      <div className="member-intelligence-heading">
        <div>
          <div className="zalo-step-heading">
            <span><Activity size={16} aria-hidden="true" /></span>
            <strong>Member Intelligence</strong>
          </div>
          <p className="muted">
            Số liệu lấy từ UID thành viên, poll và tin nhắn Zalo đã đồng bộ. AI chỉ hiểu câu hỏi và diễn đạt kết quả.
          </p>
        </div>
        <button className="button-primary" type="button" onClick={startSync} disabled={isSyncing || sync?.status === "Running"}>
          <RefreshCw className={isSyncing || sync?.status === "Running" ? "spin" : ""} size={16} aria-hidden="true" />
          {sync ? "Đồng bộ lại dữ liệu cũ" : "Bắt đầu đồng bộ"}
        </button>
      </div>

      {error && <div className="notice notice-warn"><AlertTriangle size={16} aria-hidden="true" /> {error}</div>}

      {sync ? (
        <>
          <div className="member-sync-progress">
            <div>
              <small>Trạng thái</small>
              <strong>{sync.status === "CompletedWithLimitations" ? "Hoàn tất có giới hạn" : formatStage(sync.stage)}</strong>
            </div>
            <div>
              <small><Users size={14} aria-hidden="true" /> Thành viên</small>
              <strong>{sync.membersSynchronized}</strong>
            </div>
            <div>
              <small><Database size={14} aria-hidden="true" /> Poll</small>
              <strong>{sync.totalPollsWithVoterIdentities}/{sync.totalPollsDiscovered}</strong>
            </div>
            <div>
              <small>Tin nhắn cũ</small>
              <strong>{sync.messagesImported}</strong>
            </div>
          </div>
          <div className="member-coverage-note">
            <span>Khả năng lịch sử chat: <strong>{sync.messageHistoryCapability}</strong></span>
            <span>Poll cũ nhất: <strong>{formatDate(sync.oldestRetrievablePollAt)}</strong></span>
            <span>Tin cũ nhất: <strong>{formatDate(sync.oldestRetrievableMessageAt)}</strong></span>
            <span>Đồng bộ gần nhất: <strong>{formatDate(sync.lastIncrementalSyncAt, true)}</strong></span>
          </div>
          {sync.lastErrorSummary && <div className="notice notice-warn">{sync.lastErrorSummary}</div>}
        </>
      ) : (
        <div className="notice">
          Chưa có dữ liệu lịch sử. Nhấn “Bắt đầu đồng bộ” — tiến trình chạy nền và tiếp tục được sau khi service restart.
        </div>
      )}

      <div className="member-intelligence-toolbar">
        <label className="field">
          <span>Bộ lọc hoạt động</span>
          <select className="input" value={filterValue} onChange={(event) => setFilterValue(event.target.value as typeof filterValue)}>
            {FILTERS.map((item) => <option key={item.value} value={item.value}>{item.label}</option>)}
          </select>
        </label>
        <button className="button-secondary" type="button" onClick={() => void loadMembers(page)} disabled={isLoading}>
          <RefreshCw className={isLoading ? "spin" : ""} size={15} aria-hidden="true" /> Làm mới
        </button>
      </div>

      {activityPage?.coverage.warning && (
        <div className="notice notice-warn">{activityPage.coverage.warning}</div>
      )}

      <div className="member-intelligence-table-wrap">
        <table className="member-intelligence-table">
          <thead>
            <tr>
              <th>Thành viên</th>
              <th>Tin gần nhất</th>
              <th>Poll gần nhất có vote</th>
              <th>Vote</th>
              <th>Tin nhắn</th>
              <th>Trạng thái</th>
              <th>Độ tin cậy</th>
            </tr>
          </thead>
          <tbody>
            {activityPage?.items.map((member) => (
              <tr key={member.zaloUserId}>
                <td>
                  <div className="member-identity">
                    {member.avatarUrl
                      ? <img src={member.avatarUrl} alt="" referrerPolicy="no-referrer" />
                      : <span>{member.displayName.slice(0, 1).toUpperCase()}</span>}
                    <div>
                      <strong>{member.displayName}</strong>
                      <small>{member.zaloUserId}</small>
                    </div>
                  </div>
                </td>
                <td>{formatDate(member.lastMessageAt, true)}</td>
                <td>
                  {member.lastVotedPollQuestion
                    ? <><strong>{member.lastVotedPollQuestion}</strong><small>{formatDate(member.lastVotedPollCreatedAt)}</small></>
                    : "Chưa ghi nhận"}
                </td>
                <td>
                  {member.voteParticipationRate === null
                    ? "—"
                    : `${Math.round(member.voteParticipationRate * 100)}%`}
                  <small>{member.votedPollCount}/{member.eligiblePollCount} poll</small>
                </td>
                <td>{member.messageCount}<small>{member.activeMessageDays} ngày hoạt động</small></td>
                <td><Badge tone={statusTone(member.engagementStatus)}>{statusLabel(member.engagementStatus)}</Badge></td>
                <td>{member.dataConfidence}</td>
              </tr>
            ))}
          </tbody>
        </table>
        {!isLoading && activityPage?.items.length === 0 && (
          <div className="member-intelligence-empty">Không có thành viên phù hợp với bộ lọc này.</div>
        )}
        {isLoading && <div className="member-intelligence-empty"><RefreshCw className="spin" size={20} /> Đang tính dữ liệu…</div>}
      </div>

      {activityPage && activityPage.totalPages > 1 && (
        <div className="pagination-row">
          <button className="button-ghost" type="button" disabled={page <= 1 || isLoading} onClick={() => setPage((value) => value - 1)}>
            ← Trước
          </button>
          <span>Trang {activityPage.page}/{activityPage.totalPages} · {activityPage.totalItems} thành viên</span>
          <button className="button-ghost" type="button" disabled={page >= activityPage.totalPages || isLoading} onClick={() => setPage((value) => value + 1)}>
            Sau →
          </button>
        </div>
      )}
    </div>
  );
}
