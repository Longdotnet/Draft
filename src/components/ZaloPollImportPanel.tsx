import { useEffect, useMemo, useState } from "react";
import { CheckCircle2, Link2, QrCode, RefreshCw, UserPlus, Vote } from "lucide-react";
import {
  apiFetch,
  type DbGender,
  type DbLevel,
  type DbRole,
  type SessionResponse,
  type StartZaloQrLoginResponse,
  type ZaloConnectionResponse,
  type ZaloGroupResponse,
  type ZaloImportCandidateResponse,
  type ZaloImportPreviewResponse,
  type ZaloPollImportResultResponse,
  type ZaloPollResponse,
  type ZaloQrLoginStatusResponse,
} from "../api/dbClient";
import { Badge } from "./ui";

type CandidateDraft = Omit<ZaloImportCandidateResponse, "gender"> & {
  gender: DbGender | null;
  include: boolean;
};

const genderOptions: Array<{ value: DbGender; label: string }> = [
  { value: "Male", label: "Nam" },
  { value: "Female", label: "Nữ" },
  { value: "Unknown", label: "Chưa xác định" },
];

const roleOptions: Array<{ value: DbRole; label: string }> = [
  { value: "Attack", label: "Tấn công" },
  { value: "Defense", label: "Thủ" },
  { value: "Setter", label: "Chuyền hai" },
  { value: "FullStack", label: "Toàn diện" },
  { value: "New", label: "Mới" },
];

const levelOptions: Array<{ value: DbLevel; label: string }> = [
  { value: "Good", label: "Tốt" },
  { value: "Average", label: "Trung bình" },
  { value: "New", label: "Người mới" },
];

export function ZaloPollImportPanel({
  token,
  session,
  onSessionUpdated,
  onImported,
}: {
  token: string;
  session: SessionResponse;
  onSessionUpdated: (session: SessionResponse) => void;
  onImported: () => Promise<void>;
}) {
  const [connections, setConnections] = useState<ZaloConnectionResponse[]>([]);
  const [selectedConnectionId, setSelectedConnectionId] = useState(session.zaloConnectionId ?? "");
  const [qrLoginId, setQrLoginId] = useState<string | null>(null);
  const [qrStatus, setQrStatus] = useState<ZaloQrLoginStatusResponse | null>(null);
  const [groups, setGroups] = useState<ZaloGroupResponse[]>([]);
  const [selectedGroupId, setSelectedGroupId] = useState(session.zaloGroupId ?? "");
  const [polls, setPolls] = useState<ZaloPollResponse[]>([]);
  const [selectedPollId, setSelectedPollId] = useState("");
  const [selectedOptionIds, setSelectedOptionIds] = useState<string[]>([]);
  const [preview, setPreview] = useState<ZaloImportPreviewResponse | null>(null);
  const [candidateDrafts, setCandidateDrafts] = useState<CandidateDraft[]>([]);
  const [isBusy, setIsBusy] = useState(false);
  const [error, setError] = useState<string | null>(null);
  const [message, setMessage] = useState<string | null>(null);

  const selectedPoll = polls.find((poll) => poll.id === selectedPollId) ?? null;
  const canEditRoster = session.status !== "Drafting" && session.status !== "Finished";
  const includedCandidates = candidateDrafts.filter((candidate) => candidate.include);
  const unresolvedGenderCount = includedCandidates.filter((candidate) => candidate.gender === null).length;
  const canDivideIncluded =
    includedCandidates.length >= session.teamCount * 2 &&
    includedCandidates.length % session.teamCount === 0;

  useEffect(() => {
    void loadConnections();
  }, [token]);

  useEffect(() => {
    setSelectedConnectionId(session.zaloConnectionId ?? "");
    setSelectedGroupId(session.zaloGroupId ?? "");
    setPolls([]);
    setPreview(null);
  }, [session.id, session.zaloConnectionId, session.zaloGroupId]);

  useEffect(() => {
    if (!qrLoginId) return;
    const timer = window.setInterval(() => void pollQrLogin(qrLoginId), 1800);
    void pollQrLogin(qrLoginId);
    return () => window.clearInterval(timer);
  }, [qrLoginId]);

  async function run<T>(action: () => Promise<T>): Promise<T | null> {
    setIsBusy(true);
    setError(null);
    setMessage(null);
    try {
      return await action();
    } catch (caught) {
      setError(caught instanceof Error ? caught.message : "Không thể xử lý dữ liệu Zalo.");
      return null;
    } finally {
      setIsBusy(false);
    }
  }

  async function loadConnections() {
    const result = await run(() =>
      apiFetch<ZaloConnectionResponse[]>("/zalo/connections", { token }),
    );
    if (result) {
      setConnections(result);
      if (!selectedConnectionId && result.length === 1) setSelectedConnectionId(result[0].id);
    }
  }

  async function startQrLogin() {
    const result = await run(() =>
      apiFetch<StartZaloQrLoginResponse>("/zalo/connections/qr", { method: "POST", token }),
    );
    if (!result) return;
    setQrLoginId(result.loginId);
    setQrStatus({
      loginId: result.loginId,
      status: result.status,
      qrImageBase64: null,
      displayName: null,
      avatarUrl: null,
      error: null,
      connection: null,
    });
  }

  async function pollQrLogin(loginId: string) {
    try {
      const result = await apiFetch<ZaloQrLoginStatusResponse>(
        `/zalo/connections/qr/${encodeURIComponent(loginId)}`,
        { token },
      );
      setQrStatus(result);
      if (result.status === "completed" && result.connection) {
        setQrLoginId(null);
        setSelectedConnectionId(result.connection.id);
        setMessage(`Đã kết nối Zalo ${result.connection.displayName}.`);
        await loadConnections();
      } else if (["expired", "declined", "failed"].includes(result.status)) {
        setQrLoginId(null);
        setError(result.error || "Phiên QR không hoàn tất. Hãy tạo QR mới.");
      }
    } catch (caught) {
      setQrLoginId(null);
      setError(caught instanceof Error ? caught.message : "Không đọc được trạng thái QR.");
    }
  }

  async function loadGroups() {
    if (!selectedConnectionId) return;
    const result = await run(() =>
      apiFetch<ZaloGroupResponse[]>(
        `/zalo/connections/${encodeURIComponent(selectedConnectionId)}/groups`,
        { token },
      ),
    );
    if (result) setGroups(result);
  }

  async function linkGroup() {
    if (!selectedConnectionId || !selectedGroupId) return;
    const result = await run(() =>
      apiFetch<SessionResponse>(`/sessions/${session.id}/zalo-group`, {
        method: "PUT",
        token,
        body: { connectionId: selectedConnectionId, groupId: selectedGroupId },
      }),
    );
    if (!result) return;
    onSessionUpdated(result);
    setMessage(`Đã liên kết với nhóm ${result.zaloGroupName}.`);
    setPolls([]);
    setPreview(null);
  }

  async function loadPolls() {
    const result = await run(() =>
      apiFetch<ZaloPollResponse[]>(`/sessions/${session.id}/zalo-polls`, { token }),
    );
    if (result) {
      setPolls(result);
      setSelectedPollId("");
      setSelectedOptionIds([]);
      setPreview(null);
      if (result.length === 0) setMessage("Không tìm thấy poll nào trong bảng tin nhóm.");
    }
  }

  function choosePoll(pollId: string) {
    setSelectedPollId(pollId);
    setSelectedOptionIds([]);
    setPreview(null);
  }

  function toggleOption(optionId: string) {
    setSelectedOptionIds((current) =>
      current.includes(optionId)
        ? current.filter((id) => id !== optionId)
        : [...current, optionId],
    );
    setPreview(null);
  }

  async function createPreview() {
    if (!selectedPoll || selectedOptionIds.length === 0) return;
    const result = await run(() =>
      apiFetch<ZaloImportPreviewResponse>(`/sessions/${session.id}/zalo-import-preview`, {
        method: "POST",
        token,
        body: { pollId: selectedPoll.id, selectedOptionIds },
      }),
    );
    if (!result) return;
    setPreview(result);
    setCandidateDrafts(
      result.candidates.map((candidate) => ({
        ...candidate,
        include: !candidate.alreadyInSession,
      })),
    );
  }

  function updateCandidate(zaloUserId: string, patch: Partial<CandidateDraft>) {
    setCandidateDrafts((current) =>
      current.map((candidate) =>
        candidate.zaloUserId === zaloUserId ? { ...candidate, ...patch } : candidate,
      ),
    );
  }

  async function confirmImport() {
    if (!preview || includedCandidates.length === 0 || unresolvedGenderCount > 0) return;
    const result = await run(() =>
      apiFetch<ZaloPollImportResultResponse>(`/sessions/${session.id}/zalo-import`, {
        method: "POST",
        token,
        body: {
          pollId: preview.pollId,
          selectedOptionIds: preview.selectedOptions.map((option) => option.id),
          expectedPollUpdatedAtUnixMs: preview.pollUpdatedAtUnixMs,
          candidates: candidateDrafts.map((candidate) => ({
            zaloUserId: candidate.zaloUserId,
            include: candidate.include,
            gender: candidate.gender ?? "Unknown",
            role: candidate.role,
            level: candidate.level,
          })),
        },
      }),
    );
    if (!result) return;
    setMessage(result.message);
    setPreview(null);
    setCandidateDrafts([]);
    await onImported();
  }

  const linkedGroup = useMemo(
    () => groups.find((group) => group.id === selectedGroupId) ?? null,
    [groups, selectedGroupId],
  );

  return (
    <div className="tool-panel zalo-import-panel">
      <div className="card-title-row">
        <div>
          <h2>Nhập người chơi từ bình chọn Zalo</h2>
          <p className="muted">Kết nối tài khoản, chọn nhóm, poll và kiểm tra danh sách trước khi import.</p>
        </div>
        {session.zaloGroupName && <Badge tone="sky">{session.zaloGroupName}</Badge>}
      </div>

      {message && <div className="notice notice-good">{message}</div>}
      {error && <div className="notice notice-danger">{error}</div>}
      {!canEditRoster && (
        <div className="notice notice-warn">Buổi đấu đã bắt đầu nên không thể liên kết nhóm hoặc import thêm người.</div>
      )}

      <div className="zalo-step">
        <div className="zalo-step-heading"><span>1</span><strong>Kết nối Zalo</strong></div>
        <div className="action-row">
          <select
            className="input"
            value={selectedConnectionId}
            onChange={(event) => {
              setSelectedConnectionId(event.target.value);
              setGroups([]);
              setSelectedGroupId("");
            }}
          >
            <option value="">Chọn tài khoản Zalo đã kết nối</option>
            {connections.map((connection) => (
              <option key={connection.id} value={connection.id}>
                {connection.displayName} · {connection.status}
              </option>
            ))}
          </select>
          <button className="button-secondary" type="button" onClick={startQrLogin} disabled={isBusy || Boolean(qrLoginId)}>
            <QrCode size={17} aria-hidden="true" /> Kết nối bằng QR
          </button>
        </div>

        {qrStatus && qrStatus.status !== "completed" && (
          <div className="zalo-qr-box">
            {qrStatus.qrImageBase64 ? (
              <img src={`data:image/png;base64,${qrStatus.qrImageBase64}`} alt="QR đăng nhập Zalo" />
            ) : (
              <RefreshCw className="spin" size={28} aria-hidden="true" />
            )}
            <div>
              <strong>{qrStatus.status === "waiting_confirm" ? "Xác nhận trên điện thoại" : "Quét QR bằng ứng dụng Zalo"}</strong>
              <p className="muted">QR chỉ tồn tại trong thời gian ngắn. Credential không được gửi về trình duyệt.</p>
            </div>
          </div>
        )}
      </div>

      <div className="zalo-step">
        <div className="zalo-step-heading"><span>2</span><strong>Liên kết nhóm</strong></div>
        <div className="action-row">
          <button className="button-secondary" type="button" onClick={loadGroups} disabled={!selectedConnectionId || isBusy}>
            <RefreshCw size={17} aria-hidden="true" /> Lấy danh sách nhóm
          </button>
          {groups.length > 0 && (
            <select className="input" value={selectedGroupId} onChange={(event) => setSelectedGroupId(event.target.value)}>
              <option value="">Chọn nhóm Zalo</option>
              {groups.map((group) => (
                <option key={group.id} value={group.id}>{group.name} · {group.totalMembers} thành viên</option>
              ))}
            </select>
          )}
          <button className="button-primary" type="button" onClick={linkGroup} disabled={!linkedGroup || isBusy || !canEditRoster}>
            <Link2 size={17} aria-hidden="true" /> Liên kết nhóm
          </button>
        </div>
      </div>

      <div className="zalo-step">
        <div className="zalo-step-heading"><span>3</span><strong>Chọn poll và option</strong></div>
        <button className="button-secondary" type="button" onClick={loadPolls} disabled={!session.zaloGroupId || isBusy}>
          <Vote size={17} aria-hidden="true" /> Lấy thông tin bình chọn từ Zalo
        </button>

        {polls.length > 0 && (
          <div className="zalo-poll-list">
            {polls.map((poll) => (
              <button
                className={selectedPollId === poll.id ? "zalo-poll-card active" : "zalo-poll-card"}
                key={poll.id}
                type="button"
                onClick={() => choosePoll(poll.id)}
                disabled={poll.isAnonymous}
              >
                <strong>{poll.question}</strong>
                <small>{poll.options.length} option · {poll.uniqueVoteCount} người vote</small>
                <div className="badge-row">
                  {poll.allowMultipleChoices && <Badge tone="violet">Chọn nhiều</Badge>}
                  {poll.isClosed && <Badge tone="neutral">Đã đóng</Badge>}
                  {poll.isAnonymous && <Badge tone="orange">Ẩn danh · không import được</Badge>}
                </div>
              </button>
            ))}
          </div>
        )}

        {selectedPoll && (
          <div className="zalo-option-list">
            {selectedPoll.options.map((option) => (
              <label className="zalo-option-row" key={option.id}>
                <input
                  type="checkbox"
                  checked={selectedOptionIds.includes(option.id)}
                  onChange={() => toggleOption(option.id)}
                />
                <span>{option.content}</span>
                <strong>{option.voteCount}</strong>
              </label>
            ))}
            <button className="button-primary" type="button" onClick={createPreview} disabled={selectedOptionIds.length === 0 || isBusy || !canEditRoster}>
              Xem trước người chơi
            </button>
          </div>
        )}
      </div>

      {preview && (
        <div className="zalo-step">
          <div className="zalo-step-heading"><span>4</span><strong>Preview và xác nhận</strong></div>
          <div className={canDivideIncluded ? "notice notice-good" : "notice notice-warn"}>
            Đang chọn {includedCandidates.length} người. {canDivideIncluded
              ? `Có thể chia ${session.teamCount} team, ${includedCandidates.length / session.teamCount} người/team.`
              : `Cần ít nhất ${session.teamCount * 2} người và tổng số chia hết cho ${session.teamCount}.`}
          </div>
          {unresolvedGenderCount > 0 && (
            <div className="notice notice-warn">Còn {unresolvedGenderCount} người cần chọn giới tính.</div>
          )}

          <div className="zalo-candidate-list">
            {candidateDrafts.map((candidate) => (
              <article className={candidate.include ? "zalo-candidate active" : "zalo-candidate"} key={candidate.zaloUserId}>
                <label className="zalo-candidate-main">
                  <input
                    type="checkbox"
                    checked={candidate.include}
                    onChange={(event) => updateCandidate(candidate.zaloUserId, { include: event.target.checked })}
                  />
                  {candidate.avatarUrl ? (
                    <img src={candidate.avatarUrl} alt="" referrerPolicy="no-referrer" />
                  ) : (
                    <div className="zalo-avatar-placeholder">{candidate.displayName.slice(0, 1).toUpperCase()}</div>
                  )}
                  <div>
                    <strong>{candidate.displayName}</strong>
                    <small>{candidate.optionNames.join(" · ")}</small>
                    {candidate.alreadyInSession && <Badge tone="neutral">Đã có trong buổi</Badge>}
                  </div>
                </label>
                <div className="zalo-candidate-fields">
                  <select
                    className="input"
                    aria-label={`Giới tính của ${candidate.displayName}`}
                    value={candidate.gender ?? ""}
                    disabled={!candidate.include}
                    onChange={(event) => updateCandidate(candidate.zaloUserId, { gender: event.target.value as DbGender })}
                  >
                    <option value="" disabled>Chọn giới tính</option>
                    {genderOptions.map((option) => <option key={option.value} value={option.value}>{option.label}</option>)}
                  </select>
                  <select
                    className="input"
                    value={candidate.role}
                    disabled={!candidate.include}
                    onChange={(event) => updateCandidate(candidate.zaloUserId, { role: event.target.value as DbRole })}
                  >
                    {roleOptions.map((option) => <option key={option.value} value={option.value}>{option.label}</option>)}
                  </select>
                  <select
                    className="input"
                    value={candidate.level}
                    disabled={!candidate.include}
                    onChange={(event) => updateCandidate(candidate.zaloUserId, { level: event.target.value as DbLevel })}
                  >
                    {levelOptions.map((option) => <option key={option.value} value={option.value}>{option.label}</option>)}
                  </select>
                </div>
              </article>
            ))}
          </div>

          <button
            className="button-primary"
            type="button"
            onClick={confirmImport}
            disabled={isBusy || !canEditRoster || includedCandidates.length === 0 || unresolvedGenderCount > 0}
          >
            <UserPlus size={17} aria-hidden="true" /> Xác nhận import {includedCandidates.length} người
          </button>
          {unresolvedGenderCount === 0 && includedCandidates.length > 0 && (
            <span className="zalo-ready"><CheckCircle2 size={16} aria-hidden="true" /> Dữ liệu giới tính đã hợp lệ</span>
          )}
        </div>
      )}
    </div>
  );
}
