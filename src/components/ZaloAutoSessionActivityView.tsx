import { AlertTriangle, CheckCircle2, Clock3, RefreshCw, ShieldCheck, XCircle } from "lucide-react";

export type AutoSessionCandidateActivity = {
  optionId: string;
  optionContent: string;
  dayKey: string;
  startTime: string;
  voteCount: number;
  sessionId: string | null;
  sessionName: string | null;
  sessionStatus: string | null;
  presentPlayerCount: number | null;
  capacity: number | null;
  lastRosterSyncAt: string | null;
};

export type AutoSessionProposalActivity = {
  id: string;
  pollId: string;
  pollQuestion: string;
  pollCreatorId: string;
  status: string;
  classifierConfidence: number;
  classifierReason: string;
  proposalMessageId: string | null;
  approvedByZaloUserId: string | null;
  approvedAt: string | null;
  lastError: string | null;
  createdAt: string;
  updatedAt: string;
  candidates: AutoSessionCandidateActivity[];
};

export type AutoSessionActivity = {
  trackedGroupId: string;
  groupId: string;
  groupName: string;
  autoSessionEnabled: boolean;
  proposalCount: number;
  awaitingApprovalCount: number;
  createdCount: number;
  failedCount: number;
  proposals: AutoSessionProposalActivity[];
};

type Props = {
  activity: AutoSessionActivity | null | undefined;
};

const statusMeta: Record<string, { label: string; background: string; color: string }> = {
  AwaitingApproval: { label: "Chờ duyệt", background: "rgba(245,158,11,.16)", color: "#fcd34d" },
  Created: { label: "Đã tạo", background: "rgba(34,197,94,.16)", color: "#86efac" },
  Approved: { label: "Đã duyệt", background: "rgba(59,130,246,.16)", color: "#93c5fd" },
  Rejected: { label: "Từ chối", background: "rgba(100,116,139,.25)", color: "#cbd5e1" },
  Failed: { label: "Lỗi", background: "rgba(239,68,68,.16)", color: "#fca5a5" },
  Superseded: { label: "Hết hiệu lực", background: "rgba(168,85,247,.16)", color: "#d8b4fe" },
  Ignored: { label: "Bỏ qua", background: "rgba(100,116,139,.18)", color: "#94a3b8" },
};

function formatDate(value: string | null) {
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

function formatStart(value: string) {
  const date = new Date(value);
  if (Number.isNaN(date.getTime())) return value;
  return new Intl.DateTimeFormat("vi-VN", {
    weekday: "short",
    day: "2-digit",
    month: "2-digit",
    hour: "2-digit",
    minute: "2-digit",
    timeZone: "Asia/Ho_Chi_Minh",
  }).format(date);
}

function StatusIcon({ status }: { status: string }) {
  if (status === "Created") return <CheckCircle2 size={15} />;
  if (status === "Failed") return <XCircle size={15} />;
  if (status === "AwaitingApproval") return <Clock3 size={15} />;
  return <ShieldCheck size={15} />;
}

export function ZaloAutoSessionActivityView({ activity }: Props) {
  if (!activity) {
    return (
      <div style={{ marginTop: 18, padding: 14, borderRadius: 12, background: "rgba(30,41,59,.45)", color: "#94a3b8" }}>
        Chưa có dữ liệu audit Auto Session cho group này.
      </div>
    );
  }

  return (
    <div style={{ marginTop: 20 }}>
      <div style={{ display: "flex", justifyContent: "space-between", gap: 12, alignItems: "center", flexWrap: "wrap" }}>
        <div>
          <strong>Lịch sử Auto Session</strong>
          <div style={{ color: "#94a3b8", fontSize: 13, marginTop: 3 }}>
            {activity.proposalCount} poll gần nhất · {activity.awaitingApprovalCount} chờ duyệt · {activity.createdCount} đã tạo · {activity.failedCount} lỗi
          </div>
        </div>
        <span style={{ color: activity.autoSessionEnabled ? "#86efac" : "#94a3b8", fontSize: 13 }}>
          {activity.autoSessionEnabled ? "● Listener đang theo dõi" : "○ Auto Session đang tắt"}
        </span>
      </div>

      {activity.proposals.length === 0 ? (
        <div style={{ marginTop: 12, color: "#64748b" }}>Chưa ghi nhận poll nào.</div>
      ) : (
        <div style={{ display: "grid", gap: 12, marginTop: 12 }}>
          {activity.proposals.map((proposal) => {
            const meta = statusMeta[proposal.status] ?? {
              label: proposal.status,
              background: "rgba(100,116,139,.18)",
              color: "#cbd5e1",
            };
            return (
              <article
                key={proposal.id}
                style={{
                  border: "1px solid rgba(148,163,184,.16)",
                  borderRadius: 14,
                  padding: 14,
                  background: "rgba(15,23,42,.55)",
                }}
              >
                <div style={{ display: "flex", justifyContent: "space-between", gap: 10, flexWrap: "wrap" }}>
                  <div style={{ minWidth: 0 }}>
                    <div style={{ fontWeight: 750, overflowWrap: "anywhere" }}>{proposal.pollQuestion || "Poll không có tiêu đề"}</div>
                    <div style={{ color: "#64748b", fontSize: 12, marginTop: 3 }}>
                      Poll {proposal.pollId} · cập nhật {formatDate(proposal.updatedAt)}
                    </div>
                  </div>
                  <span
                    style={{
                      display: "inline-flex",
                      gap: 5,
                      alignItems: "center",
                      padding: "5px 9px",
                      borderRadius: 999,
                      background: meta.background,
                      color: meta.color,
                      fontSize: 12,
                      fontWeight: 700,
                    }}
                  >
                    <StatusIcon status={proposal.status} /> {meta.label}
                  </span>
                </div>

                <div style={{ display: "flex", gap: 12, flexWrap: "wrap", marginTop: 10, fontSize: 12, color: "#94a3b8" }}>
                  <span>AI/rule confidence: {Math.round(proposal.classifierConfidence * 100)}%</span>
                  <span>Creator: {proposal.pollCreatorId || "—"}</span>
                  {proposal.approvedByZaloUserId ? <span>Duyệt bởi: {proposal.approvedByZaloUserId}</span> : null}
                  {proposal.approvedAt ? <span>Lúc duyệt: {formatDate(proposal.approvedAt)}</span> : null}
                </div>
                <div style={{ marginTop: 6, color: "#64748b", fontSize: 12, overflowWrap: "anywhere" }}>
                  Reason: {proposal.classifierReason || "—"}
                </div>

                {proposal.lastError ? (
                  <div
                    style={{
                      display: "flex",
                      gap: 7,
                      alignItems: "flex-start",
                      marginTop: 10,
                      padding: "9px 10px",
                      borderRadius: 10,
                      background: "rgba(239,68,68,.1)",
                      color: "#fca5a5",
                      fontSize: 13,
                    }}
                  >
                    <AlertTriangle size={15} style={{ flex: "0 0 auto", marginTop: 1 }} />
                    <span style={{ overflowWrap: "anywhere" }}>{proposal.lastError}</span>
                  </div>
                ) : null}

                <div style={{ display: "grid", gap: 8, marginTop: 12 }}>
                  {proposal.candidates.map((candidate) => (
                    <div
                      key={`${proposal.id}-${candidate.optionId}`}
                      style={{
                        display: "grid",
                        gridTemplateColumns: "minmax(150px,1.25fr) minmax(145px,1fr) minmax(150px,1fr)",
                        gap: 10,
                        alignItems: "center",
                        padding: "9px 10px",
                        borderRadius: 10,
                        background: "rgba(30,41,59,.52)",
                        fontSize: 13,
                      }}
                    >
                      <div>
                        <strong>{candidate.dayKey}</strong> · {candidate.optionContent}
                        <div style={{ color: "#64748b", fontSize: 12, marginTop: 2 }}>
                          {formatStart(candidate.startTime)} · snapshot {candidate.voteCount} vote
                        </div>
                      </div>
                      <div>
                        {candidate.sessionId ? (
                          <>
                            <span style={{ color: "#86efac" }}>Đã link: {candidate.sessionName ?? candidate.sessionId}</span>
                            <div style={{ color: "#64748b", fontSize: 12, marginTop: 2 }}>
                              {candidate.sessionStatus ?? "—"}
                            </div>
                          </>
                        ) : (
                          <span style={{ color: "#94a3b8" }}>Chưa tạo session</span>
                        )}
                      </div>
                      <div>
                        {candidate.presentPlayerCount !== null && candidate.capacity !== null ? (
                          <span>
                            Roster <strong>{candidate.presentPlayerCount}/{candidate.capacity}</strong>
                          </span>
                        ) : (
                          <span style={{ color: "#64748b" }}>Roster —</span>
                        )}
                        <div style={{ color: "#64748b", fontSize: 12, marginTop: 2 }}>
                          <RefreshCw size={11} style={{ verticalAlign: "-1px", marginRight: 4 }} />
                          Sync cuối: {formatDate(candidate.lastRosterSyncAt)}
                        </div>
                      </div>
                    </div>
                  ))}
                </div>
              </article>
            );
          })}
        </div>
      )}
    </div>
  );
}
