import { useEffect, useMemo, useState } from "react";
import { Bell, Bot, CheckCircle2, Link2, MapPin, QrCode, RefreshCw, Save, UserPlus, Vote } from "lucide-react";
import {
  apiFetch,
  type DbGender,
  type DbLevel,
  type DbRole,
  type SessionResponse,
  type StartZaloQrLoginResponse,
  type ZaloConnectionResponse,
  type ZaloBotSettingsResponse,
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

const POLLS_PER_PAGE = 3;

function toDateTimeInput(value: string | null) {
  if (!value) return "";
  const date = new Date(value);
  const local = new Date(date.getTime() - date.getTimezoneOffset() * 60_000);
  return local.toISOString().slice(0, 16);
}

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
  const [pollPage, setPollPage] = useState(1);
  const [selectedPollId, setSelectedPollId] = useState("");
  const [selectedOptionIds, setSelectedOptionIds] = useState<string[]>([]);
  const [preview, setPreview] = useState<ZaloImportPreviewResponse | null>(null);
  const [candidateDrafts, setCandidateDrafts] = useState<CandidateDraft[]>([]);
  const [isBusy, setIsBusy] = useState(false);
  const [error, setError] = useState<string | null>(null);
  const [message, setMessage] = useState<string | null>(null);
  const [botSettings, setBotSettings] = useState({
    startTime: toDateTimeInput(session.startTime),
    location: session.location ?? "",
    parkingInstructions: session.parkingInstructions ?? "",
    locationImageUrl: session.locationImageUrl ?? "",
    botEnabled: session.botEnabled,
    botCustomInstructions: session.botCustomInstructions ?? "",
    reminderEnabled: session.reminderEnabled,
    reminderLeadHours: session.reminderLeadHours,
    reminderIntervalHours: session.reminderIntervalHours,
  });

  const selectedPoll = polls.find((poll) => poll.id === selectedPollId) ?? null;
  const totalPollPages = Math.max( 1, Math.ceil(polls.length / POLLS_PER_PAGE),);

  const visiblePolls = useMemo(() => {
    const startIndex = (pollPage - 1) * POLLS_PER_PAGE;

    return polls.slice(startIndex,startIndex + POLLS_PER_PAGE,);
  }, [polls, pollPage]);
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
    setPollPage(1);
    setPreview(null);
    setBotSettings({
      startTime: toDateTimeInput(session.startTime),
      location: session.location ?? "",
      parkingInstructions: session.parkingInstructions ?? "",
      locationImageUrl: session.locationImageUrl ?? "",
      botEnabled: session.botEnabled,
      botCustomInstructions: session.botCustomInstructions ?? "",
      reminderEnabled: session.reminderEnabled,
      reminderLeadHours: session.reminderLeadHours,
      reminderIntervalHours: session.reminderIntervalHours,
    });
  }, [session.id, session.zaloConnectionId, session.zaloGroupId, session.startTime, session.location, session.parkingInstructions, session.locationImageUrl, session.botEnabled, session.botCustomInstructions, session.reminderEnabled, session.reminderLeadHours, session.reminderIntervalHours]);

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
    setPollPage(1);
    setPreview(null);
  }

  async function loadPolls() {
    const result = await run(() =>
      apiFetch<ZaloPollResponse[]>(`/sessions/${session.id}/zalo-polls`, { token }),
    );
    if (result) {
      setPolls(result);
      setPollPage(1);
      setSelectedPollId("");
      setSelectedOptionIds([]);
      setPreview(null);
      if (result.length === 0) setMessage("Không tìm thấy poll nào trong bảng tin nhóm.");
    }
  }

  async function saveBotSettings() {
    const result = await run(() =>
      apiFetch<ZaloBotSettingsResponse>(`/sessions/${session.id}/zalo-bot-settings`, {
        method: "PUT",
        token,
        body: {
          ...botSettings,
          startTime: botSettings.startTime ? new Date(botSettings.startTime).toISOString() : null,
          location: botSettings.location || null,
          parkingInstructions: botSettings.parkingInstructions || null,
          locationImageUrl: botSettings.locationImageUrl || null,
          botCustomInstructions: botSettings.botCustomInstructions || null,
        },
      }),
    );
    if (!result) return;
    onSessionUpdated({
      ...session,
      startTime: result.startTime,
      location: result.location,
      parkingInstructions: result.parkingInstructions,
      locationImageUrl: result.locationImageUrl,
      botEnabled: result.botEnabled,
      botCustomInstructions: result.botCustomInstructions,
      reminderEnabled: result.reminderEnabled,
      reminderLeadHours: result.reminderLeadHours,
      reminderIntervalHours: result.reminderIntervalHours,
      lastReminderAt: result.lastReminderAt,
    });
    setMessage(result.botEnabled ? "Đã lưu cấu hình và bật listener cho bot." : "Đã lưu cấu hình; bot đang tắt.");
  }

  function choosePoll(pollId: string) {
    setSelectedPollId(pollId);
    setSelectedOptionIds([]);
    setPreview(null);
  }
  function changePollPage(nextPage: number) {
    const safePage = Math.min(
      Math.max(nextPage, 1),
      totalPollPages,
    );

    setPollPage(safePage);

    // Tránh trường hợp poll đang chọn bị ẩn ở trang khác.
    setSelectedPollId("");
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

      <div className="zalo-step zalo-login-step">
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

      <div className="zalo-step zalo-bot-settings">
        <div className="zalo-step-heading"><span><Bot size={16} aria-hidden="true" /></span><strong>Bot chat & reminder</strong></div>
        <p className="muted">Bot dùng lịch sử chat gần đây, dữ liệu session/poll và thông tin người hỏi. Mỗi tài khoản chỉ chạy một listener cho tất cả group đã bật.</p>

        <div className="zalo-toggle-row">
          <label className="zalo-toggle-card">
            <input
              type="checkbox"
              checked={botSettings.botEnabled}
              disabled={!session.zaloGroupId}
              onChange={(event) => setBotSettings((current) => ({
                ...current,
                botEnabled: event.target.checked,
                reminderEnabled: event.target.checked ? current.reminderEnabled : false,
              }))}
            />
            <span><strong>Bật bot trong group</strong><small>Chỉ trả lời khi có @mention đúng tài khoản bot.</small></span>
          </label>
          <label className="zalo-toggle-card">
            <input
              type="checkbox"
              checked={botSettings.reminderEnabled}
              disabled={!botSettings.botEnabled || !botSettings.startTime}
              onChange={(event) => setBotSettings((current) => ({ ...current, reminderEnabled: event.target.checked }))}
            />
            <span><strong><Bell size={15} aria-hidden="true" /> Reminder @all</strong><small>Bỏ qua trận đủ slot và ưu tiên trận gần nhất.</small></span>
          </label>
        </div>

        <div className="zalo-settings-grid">
          <label className="field">
            <span>Thời gian trận</span>
            <small className="field-help">Ngày giờ thi đấu thật theo giờ máy admin. Bot dùng mốc này để trả lời giờ trận và tính reminder.</small>
            <input
              className="input"
              type="datetime-local"
              value={botSettings.startTime}
              onChange={(event) => setBotSettings((current) => ({ ...current, startTime: event.target.value }))}
            />
          </label>
          <label className="field">
            <span><MapPin size={14} aria-hidden="true" /> Địa điểm</span>
            <input
              className="input"
              placeholder="Sân UTE"
              value={botSettings.location}
              onChange={(event) => setBotSettings((current) => ({ ...current, location: event.target.value }))}
            />
          </label>
          <label className="field zalo-settings-wide">
            <span>Hướng dẫn gửi xe</span>
            <textarea
              className="input"
              rows={2}
              placeholder="Vào cổng A, gửi xe bên trái nhà thi đấu..."
              value={botSettings.parkingInstructions}
              onChange={(event) => setBotSettings((current) => ({ ...current, parkingInstructions: event.target.value }))}
            />
          </label>
          <label className="field zalo-settings-wide">
            <span>URL ảnh vị trí / sơ đồ gửi xe</span>
            <small className="field-help">Phải là link http/https mà bridge có thể tải được. Bot sẽ gửi ảnh này khi trả lời location.</small>
            <input
              className="input"
              type="url"
              placeholder="https://.../so-do-san.jpg"
              value={botSettings.locationImageUrl}
              onChange={(event) => setBotSettings((current) => ({ ...current, locationImageUrl: event.target.value }))}
            />
          </label>
          <label className="field">
            <span>Bắt đầu nhắc trước trận (giờ)</span>
            <small className="field-help">72 nghĩa là bắt đầu nhắc trước 3 ngày.</small>
            <input
              className="input"
              type="number"
              min={1}
              max={336}
              value={botSettings.reminderLeadHours}
              onChange={(event) => setBotSettings((current) => ({ ...current, reminderLeadHours: Number(event.target.value) }))}
            />
          </label>
          <label className="field">
            <span>Lặp lại sau mỗi (giờ)</span>
            <small className="field-help">12 nghĩa là nếu còn thiếu slot thì 12 giờ tag @all một lần.</small>
            <input
              className="input"
              type="number"
              min={1}
              max={168}
              value={botSettings.reminderIntervalHours}
              onChange={(event) => setBotSettings((current) => ({ ...current, reminderIntervalHours: Number(event.target.value) }))}
            />
          </label>
          <label className="field zalo-settings-wide">
            <span>Ghi chú riêng cho AI</span>
            <small className="field-help">Chỉ là hướng dẫn thêm cho câu hỏi tự do; các lệnh help/location/danh sách dùng dữ liệu chính xác trong hệ thống.</small>
            <textarea
              className="input"
              rows={2}
              placeholder="Ví dụ: gọi nhóm là Longg Volley, trả lời thân thiện..."
              value={botSettings.botCustomInstructions}
              onChange={(event) => setBotSettings((current) => ({ ...current, botCustomInstructions: event.target.value }))}
            />
          </label>
        </div>

        <div className="zalo-command-help">
          <span>Gợi ý sẵn:</span>
          <code>@bot help</code>
          <code>@bot location</code>
          <code>@bot tui có trong danh sách không?</code>
          <code>@bot còn thiếu bao nhiêu slot?</code>
        </div>
        <div className="action-row">
          <button className="button-primary" type="button" onClick={saveBotSettings} disabled={!session.zaloGroupId || isBusy}>
            <Save size={17} aria-hidden="true" /> Lưu cấu hình bot
          </button>
          {session.lastReminderAt && <small className="muted">Lần nhắc gần nhất: {new Date(session.lastReminderAt).toLocaleString("vi-VN")}</small>}
        </div>
      </div>

      <div className="zalo-step zalo-link-step">
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

      <div className="zalo-step zalo-poll-step">
        <div className="zalo-step-heading"><span>4</span><strong>Chọn poll và option</strong></div>
        <button className="button-secondary" type="button" onClick={loadPolls} disabled={!session.zaloGroupId || isBusy}>
          <Vote size={17} aria-hidden="true" /> Lấy thông tin bình chọn từ Zalo
        </button>

        {polls.length > 0 && (
          <>
            <div className="zalo-poll-list">
              {visiblePolls.map((poll) => (
                <button
                  key={poll.id}
                  type="button"
                  className={
                    selectedPollId === poll.id
                      ? "zalo-poll-card active"
                      : "zalo-poll-card"
                  }
                  onClick={() => choosePoll(poll.id)}
                  disabled={poll.isAnonymous}
                >
                  <strong>{poll.question}</strong>
                  <small>
                    {poll.options.length} option · {poll.uniqueVoteCount} người vote
                  </small>

                    <div className="badge-row">
                      {poll.allowMultipleChoices && (
                        <Badge tone="violet">Chọn nhiều</Badge>
                      )}

                      {poll.isClosed && (
                        <Badge tone="neutral">Đã đóng</Badge>
                      )}

                      {poll.isAnonymous && (
                        <Badge tone="orange">
                          Ẩn danh · không import được
                        </Badge>
                      )}
                    </div>
                </button>
              ))}
            </div>

            {totalPollPages > 1 && (
              <div className="pagination-row">
                <button
                  type="button"
                  className="button-ghost"
                  disabled={pollPage === 1 || isBusy}
                  onClick={() => changePollPage(pollPage - 1)}
                >
                  ← Trước
                </button>

                <span>
                  Trang {pollPage}/{totalPollPages}
                  {" · "}
                  {polls.length} poll
                </span>

                <button
                  type="button"
                  className="button-ghost"
                  disabled={
                    pollPage === totalPollPages ||
                    isBusy
                  }
                  onClick={() => changePollPage(pollPage + 1)}
                >
                  Sau →
                </button>
              </div>
            )}
          </>
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
        <div className="zalo-step zalo-preview-step">
          <div className="zalo-step-heading"><span>5</span><strong>Preview và xác nhận</strong></div>
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
