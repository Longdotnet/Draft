import { FormEvent, useEffect, useMemo, useState } from "react";
import { Gift, LogOut, Pencil, Plus, RefreshCw, Save, ShieldCheck, Trash2, UsersRound, X } from "lucide-react";
import {
  apiFetch,
  ApiRequestError,
  type AuthResponse,
  type AuthUser,
  type CaptainsResponse,
  type DeleteResponse,
  type DbGender,
  type DbLevel,
  type DbRole,
  type DraftSlotType,
  type DraftStateResponse,
  type OpenBagResponse,
  type PrepareRevealResponse,
  type SessionPlayerResponse,
  type SessionResponse,
  type SharedSlotResponse,
} from "../api/dbClient";
import { BlindBagCard } from "./draft/BlindBagCard";
import { ShootingStarRevealModal } from "./draft/ShootingStarRevealModal";
import { Badge, ScorePill } from "./ui";
import { getRevealRarity, type RevealRarity } from "../lib/revealRarity";

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

const genderOptions: Array<{ value: DbGender; label: string }> = [
  { value: "Male", label: "Nam" },
  { value: "Female", label: "Nữ" },
];

const samplePlayers: Array<{ displayName: string; role: DbRole; level: DbLevel; gender: DbGender }> = [
  { displayName: "Nick", role: "Attack", level: "Good", gender: "Male" },
  { displayName: "Sin", role: "FullStack", level: "Good", gender: "Male" },
  { displayName: "Duy", role: "Setter", level: "Average", gender: "Male" },
  { displayName: "Long", role: "Defense", level: "Average", gender: "Male" },
  { displayName: "Bảo", role: "New", level: "New", gender: "Male" },
  { displayName: "Bình", role: "Attack", level: "Good", gender: "Male" },
  { displayName: "Nam", role: "Defense", level: "Average", gender: "Male" },
  { displayName: "Huy", role: "New", level: "New", gender: "Male" },
  { displayName: "An", role: "Attack", level: "Average", gender: "Female" },
  { displayName: "Cường", role: "FullStack", level: "Good", gender: "Male" },
  { displayName: "Minh", role: "Setter", level: "Good", gender: "Male" },
  { displayName: "Khoa", role: "Defense", level: "Average", gender: "Male" },
  { displayName: "Linh", role: "Setter", level: "Average", gender: "Female" },
  { displayName: "Tú", role: "FullStack", level: "Average", gender: "Female" },
  { displayName: "Sơn", role: "New", level: "New", gender: "Male" },
  { displayName: "Phúc", role: "Attack", level: "Good", gender: "Male" },
  { displayName: "Quân", role: "Defense", level: "Average", gender: "Male" },
  { displayName: "Vy", role: "Setter", level: "Average", gender: "Female" },
  { displayName: "Hải", role: "FullStack", level: "New", gender: "Male" },
];

const roleLabel = (role: DbRole) =>
  roleOptions.find((option) => option.value === role)?.label ?? role;

const levelLabel = (level: DbLevel) =>
  levelOptions.find((option) => option.value === level)?.label ?? level;

const genderLabel = (gender: DbGender) =>
  genderOptions.find((option) => option.value === gender)?.label ?? gender;

const toRevealSlotType = (type: DraftSlotType) => (type === "Shared" ? "shared" : "single");

type PendingDbReveal = {
  bagId: string;
  rarity: RevealRarity;
  revealedSlot: {
    id: string;
    displayName: string;
    role: string;
    gender: string;
    averageScore: number;
    type: "single" | "shared";
  };
  captainName: string;
  teamName: string;
};

export function DbDraftFlow() {
  const [token, setToken] = useState(() => localStorage.getItem("volleyDraftToken"));
  const [user, setUser] = useState<AuthUser | null>(() => {
    const rawUser = localStorage.getItem("volleyDraftUser");
    return rawUser ? (JSON.parse(rawUser) as AuthUser) : null;
  });
  const [authMode, setAuthMode] = useState<"login" | "register">("register");
  const [authForm, setAuthForm] = useState({
    displayName: "Admin",
    email: "admin@volley.local",
    password: "password123",
  });
  const [sessionName, setSessionName] = useState("Kèo bóng chuyền tối nay");
  const [session, setSession] = useState<SessionResponse | null>(null);
  const [players, setPlayers] = useState<SessionPlayerResponse[]>([]);
  const [sharedSlots, setSharedSlots] = useState<SharedSlotResponse[]>([]);
  const [captains, setCaptains] = useState<CaptainsResponse | null>(null);
  const [draftState, setDraftState] = useState<DraftStateResponse | null>(null);
  const [manualCaptainIds, setManualCaptainIds] = useState<string[]>(["", "", ""]);
  const [selectedSharedIds, setSelectedSharedIds] = useState<string[]>([]);
  const [playerForm, setPlayerForm] = useState({
    displayName: "",
    role: "Attack" as DbRole,
    level: "Average" as DbLevel,
    gender: "Male" as DbGender,
  });
  const [editingPlayerId, setEditingPlayerId] = useState<string | null>(null);
  const [playerEditForm, setPlayerEditForm] = useState({
    displayName: "",
    role: "Attack" as DbRole,
    level: "Average" as DbLevel,
    gender: "Male" as DbGender,
    isPresent: true,
    isCaptainEligible: true,
  });
  const [statusMessage, setStatusMessage] = useState<string | null>(null);
  const [error, setError] = useState<string | null>(null);
  const [isBusy, setIsBusy] = useState(false);
  const [pendingReveal, setPendingReveal] = useState<PendingDbReveal | null>(null);

  const availableForShared = useMemo(
    () => players.filter((player) => !player.isInsideSharedSlot),
    [players],
  );
  const draftReady = draftState?.sessionStatus === "Drafting";
  const canEditRoster = session?.status !== "Drafting" && session?.status !== "Finished";

  useEffect(() => {
    if (!token || !session || draftState?.sessionStatus !== "Drafting") {
      return;
    }

    const timer = window.setInterval(() => {
      void refreshDraftState();
    }, 2000);

    return () => window.clearInterval(timer);
  }, [token, session, draftState?.sessionStatus]);

  useEffect(() => {
    if (!token || !user || session) {
      return;
    }

    void loadLatestSession();
  }, [token, user, session]);

  async function runAction<T>(action: () => Promise<T>, successMessage?: string) {
    setIsBusy(true);
    setError(null);
    try {
      const result = await action();
      if (successMessage) {
        setStatusMessage(successMessage);
      }
      return result;
    } catch (caught) {
      if (caught instanceof ApiRequestError && caught.status === 401) {
        logout();
      }
      setError(caught instanceof Error ? caught.message : "Có lỗi xảy ra.");
      return null;
    } finally {
      setIsBusy(false);
    }
  }

  async function handleAuth(event: FormEvent<HTMLFormElement>) {
    event.preventDefault();
    await runAction(async () => {
      const response = await apiFetch<AuthResponse>(
        authMode === "login" ? "/auth/login" : "/auth/register",
        {
          method: "POST",
          body:
            authMode === "login"
              ? { email: authForm.email, password: authForm.password }
              : authForm,
        },
      );
      setToken(response.token);
      setUser(response.user);
      localStorage.setItem("volleyDraftToken", response.token);
      localStorage.setItem("volleyDraftUser", JSON.stringify(response.user));
      return response;
    }, "Đã đăng nhập admin.");
  }

  function logout() {
    setToken(null);
    setUser(null);
    setSession(null);
    setPlayers([]);
    setSharedSlots([]);
    setCaptains(null);
    setDraftState(null);
    localStorage.removeItem("volleyDraftToken");
    localStorage.removeItem("volleyDraftUser");
  }

  async function createSession() {
    if (!token) return;
    await runAction(async () => {
      const createdSession = await apiFetch<SessionResponse>("/sessions", {
        method: "POST",
        token,
        body: { name: sessionName, teamCount: 3, teamSize: 6, totalSets: 4 },
      });
      setSession(createdSession);
      setSessionName(createdSession.name);
      setPlayers([]);
      setSharedSlots([]);
      setCaptains(null);
      setDraftState(null);
      return createdSession;
    }, "Đã tạo match session trong database.");
  }

  async function saveSession() {
    if (!token || !session || !sessionName.trim()) return;
    await runAction(async () => {
      const updatedSession = await apiFetch<SessionResponse>(`/sessions/${session.id}`, {
        method: "PUT",
        token,
        body: { name: sessionName, totalSets: session.totalSets },
      });
      setSession(updatedSession);
      setSessionName(updatedSession.name);
      return updatedSession;
    }, "Đã cập nhật dữ liệu");
  }

  async function deleteSession() {
    if (!token || !session) return;
    const confirmed = window.confirm(
      `Xóa session "${session.name}" và toàn bộ player, slot, team, kết quả draft?`,
    );
    if (!confirmed) return;

    await runAction(async () => {
      const response = await apiFetch<DeleteResponse>(`/sessions/${session.id}`, {
        method: "DELETE",
        token,
      });
      setSession(null);
      setPlayers([]);
      setSharedSlots([]);
      setCaptains(null);
      setDraftState(null);
      setManualCaptainIds(["", "", ""]);
      setSelectedSharedIds([]);
      setEditingPlayerId(null);
      setStatusMessage(response.message);
      await loadLatestSession();
      return response;
    });
  }

  async function refreshSessionData(targetSession = session) {
    if (!token || !targetSession) return;
    const [freshSession, freshPlayers, freshSharedSlots] = await Promise.all([
      apiFetch<SessionResponse>(`/sessions/${targetSession.id}`, { token }),
      apiFetch<SessionPlayerResponse[]>(`/sessions/${targetSession.id}/players`, { token }),
      apiFetch<SharedSlotResponse[]>(`/sessions/${targetSession.id}/shared-slots`, { token }),
    ]);
    setSession(freshSession);
    setSessionName(freshSession.name);
    setPlayers(freshPlayers);
    setSharedSlots(freshSharedSlots);
  }

  async function loadLatestSession() {
    if (!token) return;

    try {
      const savedSessions = await apiFetch<SessionResponse[]>("/sessions", { token });
      const latestSession = savedSessions[0];
      if (!latestSession) {
        return;
      }

      setSession(latestSession);
      setSessionName(latestSession.name);
      await refreshSessionData(latestSession);
      try {
        const captainResponse = await apiFetch<CaptainsResponse>(
          `/sessions/${latestSession.id}/captains`,
          { token },
        );
        setCaptains(captainResponse);
        setManualCaptainIds(captainResponse.captains.map((captain) => captain.sessionPlayerId));
      } catch {
        setCaptains(null);
      }

      if (latestSession.status === "Drafting" || latestSession.status === "Finished") {
        const state = await apiFetch<DraftStateResponse>(`/sessions/${latestSession.id}/draft-state`, {
          token,
        });
        setDraftState(state);
      }
    } catch (caught) {
      setError(caught instanceof Error ? caught.message : "Không tải được session đã lưu.");
    }
  }

  async function addPlayer(event: FormEvent<HTMLFormElement>) {
    event.preventDefault();
    if (!token || !session || !playerForm.displayName.trim()) return;
    await runAction(async () => {
      await apiFetch<SessionPlayerResponse>(`/sessions/${session.id}/players`, {
        method: "POST",
        token,
        body: playerForm,
      });
      setPlayerForm({ displayName: "", role: "Attack", level: "Average", gender: "Male" });
      await refreshSessionData();
    }, "Đã lưu người chơi vào database.");
  }

  function startEditPlayer(player: SessionPlayerResponse) {
    setEditingPlayerId(player.id);
    setPlayerEditForm({
      displayName: player.displayName,
      role: player.role,
      level: player.level,
      gender: player.gender,
      isPresent: player.isPresent,
      isCaptainEligible: player.isCaptainEligible,
    });
  }

  function cancelEditPlayer() {
    setEditingPlayerId(null);
  }

  async function reloadCaptains() {
    if (!token || !session) return;
    try {
      const captainResponse = await apiFetch<CaptainsResponse>(
        `/sessions/${session.id}/captains`,
        { token },
      );
      setCaptains(captainResponse);
      setManualCaptainIds(captainResponse.captains.map((captain) => captain.sessionPlayerId));
    } catch {
      setCaptains(null);
      setManualCaptainIds(["", "", ""]);
    }
  }

  async function savePlayer(playerId: string) {
    if (!token || !session || !playerEditForm.displayName.trim()) return;
    await runAction(async () => {
      const response = await apiFetch<SessionPlayerResponse>(
        `/sessions/${session.id}/players/${playerId}`,
        {
          method: "PUT",
          token,
          body: playerEditForm,
        },
      );
      setEditingPlayerId(null);
      await refreshSessionData();
      await reloadCaptains();
      return response;
    }, "Đã cập nhật player.");
  }

  async function deletePlayer(player: SessionPlayerResponse) {
    if (!token || !session) return;
    const confirmed = window.confirm(`Xóa player "${player.displayName}" khỏi session?`);
    if (!confirmed) return;

    await runAction(async () => {
      const response = await apiFetch<DeleteResponse>(
        `/sessions/${session.id}/players/${player.id}`,
        { method: "DELETE", token },
      );
      if (editingPlayerId === player.id) {
        setEditingPlayerId(null);
      }
      await refreshSessionData();
      await reloadCaptains();
      return response;
    }, "Đã xóa player.");
  }

  async function seedSampleRoster() {
    if (!token || !session) return;
    await runAction(async () => {
      const createdPlayers: SessionPlayerResponse[] = [];
      for (const sample of samplePlayers) {
        const createdPlayer = await apiFetch<SessionPlayerResponse>(`/sessions/${session.id}/players`, {
          method: "POST",
          token,
          body: sample,
        });
        createdPlayers.push(createdPlayer);
      }

      const bao = createdPlayers.find((player) => player.displayName === "Bảo");
      const binh = createdPlayers.find((player) => player.displayName === "Bình");
      if (bao && binh) {
        await apiFetch<SharedSlotResponse>(`/sessions/${session.id}/shared-slots`, {
          method: "POST",
          token,
          body: { sessionPlayerIds: [bao.id, binh.id], role: "Attack" },
        });
      }

      await refreshSessionData();
    }, "Đã seed 19 người chơi và slot Bảo / Bình vào database.");
  }

  function toggleSharedPlayer(playerId: string) {
    setSelectedSharedIds((current) =>
      current.includes(playerId)
        ? current.filter((id) => id !== playerId)
        : [...current, playerId],
    );
  }

  async function createSharedSlot() {
    if (!token || !session || selectedSharedIds.length < 2) return;
    await runAction(async () => {
      await apiFetch<SharedSlotResponse>(`/sessions/${session.id}/shared-slots`, {
        method: "POST",
        token,
        body: { sessionPlayerIds: selectedSharedIds, role: "Attack" },
      });
      setSelectedSharedIds([]);
      await refreshSessionData();
    }, "Đã lưu slot thay phiên vào database.");
  }

  async function deleteSharedSlot(slot: SharedSlotResponse) {
    if (!token || !session) return;
    const confirmed = window.confirm(`Xoa shared slot "${slot.displayName}"?`);
    if (!confirmed) return;

    await runAction(async () => {
      const response = await apiFetch<DeleteResponse>(
        `/sessions/${session.id}/shared-slots/${slot.id}`,
        { method: "DELETE", token },
      );
      await refreshSessionData();
      return response;
    }, "Da xoa shared slot.");
  }

  async function autoSelectCaptains() {
    if (!token || !session) return;
    await runAction(async () => {
      const response = await apiFetch<CaptainsResponse>(
        `/sessions/${session.id}/captains/auto-select`,
        { method: "POST", token },
      );
      setCaptains(response);
      setManualCaptainIds(response.captains.map((captain) => captain.sessionPlayerId));
      await refreshSessionData();
      return response;
    }, "Đã chọn 3 đại diện cân bằng.");
  }

  async function saveManualCaptains() {
    if (!token || !session) return;
    await runAction(async () => {
      const response = await apiFetch<CaptainsResponse>(`/sessions/${session.id}/captains/manual`, {
        method: "PUT",
        token,
        body: { captainSessionPlayerIds: manualCaptainIds },
      });
      setCaptains(response);
      await refreshSessionData();
      return response;
    }, "Đã lưu đại diện thủ công.");
  }

  async function startDraft() {
    if (!token || !session) return;
    await runAction(async () => {
      const response = await apiFetch<DraftStateResponse>(`/sessions/${session.id}/start-draft`, {
        method: "POST",
        token,
      });
      setDraftState(response);
      await refreshSessionData();
      return response;
    }, "Draft đã bắt đầu. Admin đưa điện thoại cho đại diện hiện tại.");
  }

  async function refreshDraftState() {
    if (!token || !session) return;
    const response = await apiFetch<DraftStateResponse>(`/sessions/${session.id}/draft-state`, {
      token,
    });
    setDraftState(response);
  }

  async function prepareReveal(bagId: string) {
    if (!token || !session || pendingReveal) return;

    await runAction(async () => {
      const response = await apiFetch<PrepareRevealResponse>(
        `/sessions/${session.id}/blind-bags/${bagId}/prepare-reveal`,
        { method: "POST", token },
      );

      setPendingReveal({
        bagId,
        rarity: getRevealRarity(response.revealedSlot.averageScore),
        revealedSlot: {
          id: response.revealedSlot.id,
          displayName: response.revealedSlot.displayName,
          role: roleLabel(response.revealedSlot.role),
          gender: genderLabel(response.revealedSlot.gender),
          averageScore: response.revealedSlot.averageScore,
          type: toRevealSlotType(response.revealedSlot.type),
        },
        captainName: response.currentCaptain.name,
        teamName: response.currentTeam.name,
      });

      return response;
    });
  }

  async function continueReveal() {
    if (!pendingReveal || !token || !session) return;

    const bagId = pendingReveal.bagId;
    await runAction(async () => {
      const response = await apiFetch<OpenBagResponse>(
        `/sessions/${session.id}/blind-bags/${bagId}/open`,
        { method: "POST", token },
      );
      setStatusMessage(null);
      await refreshDraftState();
      await refreshSessionData();
      setPendingReveal(null);
      return response;
    });
  }

  if (!token || !user) {
    return (
      <section className="screen-frame">
        <div className="screen-stack">
          <div className="screen-heading">
            <div>
              <p className="eyebrow">Admin</p>
              <h1>Đăng nhập organizer</h1>
              <p className="screen-copy">
                Backend lưu session, player list, slot thay phiên, captains và kết quả draft.
              </p>
            </div>
            <div className="heading-icon">
              <ShieldCheck size={24} aria-hidden="true" />
            </div>
          </div>

          <div className="segmented-control">
            <button
              type="button"
              className={authMode === "login" ? "active" : ""}
              onClick={() => setAuthMode("login")}
            >
              Login
            </button>
            <button
              type="button"
              className={authMode === "register" ? "active" : ""}
              onClick={() => setAuthMode("register")}
            >
              Register
            </button>
          </div>

          <form className="tool-panel" onSubmit={handleAuth}>
            <div className="form-grid">
              {authMode === "register" && (
                <label className="field">
                  <span>Tên admin</span>
                  <input
                    className="input"
                    value={authForm.displayName}
                    onChange={(event) =>
                      setAuthForm({ ...authForm, displayName: event.target.value })
                    }
                  />
                </label>
              )}
              <label className="field">
                <span>Email</span>
                <input
                  className="input"
                  value={authForm.email}
                  onChange={(event) => setAuthForm({ ...authForm, email: event.target.value })}
                />
              </label>
              <label className="field">
                <span>Password</span>
                <input
                  className="input"
                  type="password"
                  value={authForm.password}
                  onChange={(event) => setAuthForm({ ...authForm, password: event.target.value })}
                />
              </label>
            </div>
            <button className="button-primary" type="submit" disabled={isBusy}>
              <ShieldCheck size={18} aria-hidden="true" />
              {authMode === "login" ? "Đăng nhập" : "Tạo admin"}
            </button>
          </form>

          {error && <div className="notice notice-danger">{error}</div>}
        </div>
      </section>
    );
  }

  return (
    <section className="screen-frame">
      <div className="screen-stack">
        <div className="screen-heading">
          <div>
            <h1>Xếp đội hình</h1>
            <p className="screen-copy">
              Admin đăng nhập, lưu dữ liệu vào DB, rồi đưa cùng một điện thoại cho từng đại diện
              khui đúng một túi.
            </p>
          </div>
          <button className="button-ghost" type="button" onClick={logout}>
            <LogOut size={17} aria-hidden="true" />
            Logout
          </button>
        </div>


        {statusMessage && <div className="notice notice-good">{statusMessage}</div>}
        {error && <div className="notice notice-danger">{error}</div>}

        <div className="tool-panel desktop-setup-panel">
          <div className="card-title-row">
            <div>
              <h2>Buổi thi đấu</h2>
              <p className="muted">
                Session, danh sách người chơi, các slot chia sẻ và kết quả draft được lưu.
              </p>
            </div>
            {session && <Badge tone="sky">{session.status}</Badge>}
          </div>
          <div className="form-grid">
            <label className="field">
              <span>Tên buổi thi đấu</span>
              <input
                className="input"
                value={sessionName}
                onChange={(event) => setSessionName(event.target.value)}
              />
            </label>
          </div>
          <div className="action-row">
            <button className="button-primary" type="button" onClick={createSession} disabled={isBusy}>
              <Plus size={18} aria-hidden="true" />
              Tạo buổi thi đấu
            </button>
            {session && (
              <button
                className="button-secondary"
                type="button"
                onClick={() => refreshSessionData()}
              >
                <RefreshCw size={17} aria-hidden="true" />
                Refresh DB
              </button>
            )}
            {session && (
              <button className="button-secondary" type="button" onClick={saveSession} disabled={isBusy}>
                <Save size={17} aria-hidden="true" />
                Lưu session
              </button>
            )}
            {session && (
              <button className="button-danger" type="button" onClick={deleteSession} disabled={isBusy}>
                <Trash2 size={17} aria-hidden="true" />
                Xóa buổi thi đấu
              </button>
            )}
          </div>
        </div>

        {session && (
          <>
            <div className="tool-panel desktop-setup-panel">
              <div className="card-title-row">
                <div>
                  <h2>Chỉnh sửa player</h2>
                  <p className="muted">Sửa hoặc xóa player trước khi bắt đầu draft.</p>
                </div>
                {!canEditRoster && <Badge tone="orange">Đã khóa</Badge>}
              </div>
              <div className="admin-crud-list">
                {players.map((player) => {
                  const isEditing = editingPlayerId === player.id;
                  const isLocked = !canEditRoster || player.isInsideSharedSlot;

                  return (
                    <article className="admin-crud-row" key={player.id}>
                      {isEditing ? (
                        <div className="admin-edit-form">
                          <label className="field">
                            <span>Tên player</span>
                            <input
                              className="input"
                              value={playerEditForm.displayName}
                              onChange={(event) =>
                                setPlayerEditForm({
                                  ...playerEditForm,
                                  displayName: event.target.value,
                                })
                              }
                            />
                          </label>
                          <label className="field">
                            <span>Vai trò</span>
                            <select
                              className="input"
                              value={playerEditForm.role}
                              onChange={(event) =>
                                setPlayerEditForm({
                                  ...playerEditForm,
                                  role: event.target.value as DbRole,
                                })
                              }
                            >
                              {roleOptions.map((option) => (
                                <option key={option.value} value={option.value}>
                                  {option.label}
                                </option>
                              ))}
                            </select>
                          </label>
                          <label className="field">
                            <span>Trình độ</span>
                            <select
                              className="input"
                              value={playerEditForm.level}
                              onChange={(event) =>
                                setPlayerEditForm({
                                  ...playerEditForm,
                                  level: event.target.value as DbLevel,
                                })
                              }
                            >
                              {levelOptions.map((option) => (
                                <option key={option.value} value={option.value}>
                                  {option.label}
                                </option>
                              ))}
                            </select>
                          </label>
                          <label className="field">
                            <span>Giới tính</span>
                            <select
                              className="input"
                              value={playerEditForm.gender}
                              onChange={(event) =>
                                setPlayerEditForm({
                                  ...playerEditForm,
                                  gender: event.target.value as DbGender,
                                })
                              }
                            >
                              {genderOptions.map((option) => (
                                <option key={option.value} value={option.value}>
                                  {option.label}
                                </option>
                              ))}
                            </select>
                          </label>
                          <label className="toggle-line">
                            <input
                              type="checkbox"
                              checked={playerEditForm.isPresent}
                              onChange={(event) =>
                                setPlayerEditForm({
                                  ...playerEditForm,
                                  isPresent: event.target.checked,
                                })
                              }
                            />
                            Có mặt
                          </label>
                          <label className="toggle-line">
                            <input
                              type="checkbox"
                              checked={playerEditForm.isCaptainEligible}
                              onChange={(event) =>
                                setPlayerEditForm({
                                  ...playerEditForm,
                                  isCaptainEligible: event.target.checked,
                                })
                              }
                            />
                            Có thể làm captain
                          </label>
                          <div className="action-row">
                            <button
                              className="button-secondary"
                              type="button"
                              onClick={() => savePlayer(player.id)}
                              disabled={isBusy}
                            >
                              <Save size={17} aria-hidden="true" />
                              Lưu
                            </button>
                            <button className="button-ghost" type="button" onClick={cancelEditPlayer}>
                              <X size={17} aria-hidden="true" />
                              Hủy
                            </button>
                          </div>
                        </div>
                      ) : (
                        <>
                          <div>
                            <h3>{player.displayName}</h3>
                            <div className="badge-row">
                              <Badge tone="sky">{roleLabel(player.role)}</Badge>
                              <Badge tone="neutral">{levelLabel(player.level)}</Badge>
                              <Badge tone={player.gender === "Female" ? "orange" : "neutral"}>
                                {genderLabel(player.gender)}
                              </Badge>
                              {!player.isPresent && <Badge tone="orange">Absent</Badge>}
                              {!player.isCaptainEligible && <Badge tone="neutral">No captain</Badge>}
                              {player.isInsideSharedSlot && <Badge tone="violet">Shared slot</Badge>}
                            </div>
                          </div>
                          <div className="admin-card-actions">
                            <ScorePill score={player.score} />
                            <button
                              className="icon-button"
                              type="button"
                              onClick={() => startEditPlayer(player)}
                              disabled={isLocked}
                              title={
                                player.isInsideSharedSlot
                                  ? "Xóa shared slot trước khi sửa player"
                                  : "Sửa player"
                              }
                            >
                              <Pencil size={16} aria-hidden="true" />
                            </button>
                            <button
                              className="icon-button danger"
                              type="button"
                              onClick={() => deletePlayer(player)}
                              disabled={isLocked}
                              title={
                                player.isInsideSharedSlot
                                  ? "Xóa shared slot trước khi xóa player"
                                  : "Xóa player"
                              }
                            >
                              <Trash2 size={16} aria-hidden="true" />
                            </button>
                          </div>
                        </>
                      )}
                    </article>
                  );
                })}
              </div>
            </div>

            <div className="tool-panel desktop-setup-panel">
              <div className="card-title-row">
                <div>
                  <h2>Danh sách người chơi trong buổi này</h2>
                  <p className="muted">{players.length} người chơi đã lưu.</p>
                </div>
                <button
                  className="button-secondary"
                  type="button"
                  onClick={seedSampleRoster}
                  disabled={!canEditRoster || isBusy || players.length > 0}
                >
                  <Save size={17} aria-hidden="true" />
                  Seed roster mẫu
                </button>
              </div>

              <form onSubmit={addPlayer}>
                <div className="form-grid">
                  <label className="field">
                    <span>Tên người chơi</span>
                    <input
                      className="input"
                      value={playerForm.displayName}
                      onChange={(event) =>
                        setPlayerForm({ ...playerForm, displayName: event.target.value })
                      }
                    />
                  </label>
                  <label className="field">
                    <span>Vai trò</span>
                    <select
                      className="input"
                      value={playerForm.role}
                      onChange={(event) =>
                        setPlayerForm({ ...playerForm, role: event.target.value as DbRole })
                      }
                    >
                      {roleOptions.map((option) => (
                        <option key={option.value} value={option.value}>
                          {option.label}
                        </option>
                      ))}
                    </select>
                  </label>
                  <label className="field">
                    <span>Trình độ</span>
                    <select
                      className="input"
                      value={playerForm.level}
                      onChange={(event) =>
                        setPlayerForm({ ...playerForm, level: event.target.value as DbLevel })
                      }
                    >
                      {levelOptions.map((option) => (
                        <option key={option.value} value={option.value}>
                          {option.label}
                        </option>
                      ))}
                    </select>
                  </label>
                  <label className="field">
                    <span>Giới tính</span>
                    <select
                      className="input"
                      value={playerForm.gender}
                      onChange={(event) =>
                        setPlayerForm({ ...playerForm, gender: event.target.value as DbGender })
                      }
                    >
                      {genderOptions.map((option) => (
                        <option key={option.value} value={option.value}>
                          {option.label}
                        </option>
                      ))}
                    </select>
                  </label>
                  <button className="button-primary" type="submit" disabled={!canEditRoster || isBusy}>
                    <Plus size={18} aria-hidden="true" />
                    Lưu player
                  </button>
                </div>
              </form>

              <div className="card-grid">
                {players.map((player) => (
                  <article className="mini-card" key={player.id}>
                    <div>
                      <h3>{player.displayName}</h3>
                      <div className="badge-row">
                        <Badge tone="sky">{roleLabel(player.role)}</Badge>
                        <Badge tone="neutral">{levelLabel(player.level)}</Badge>
                        <Badge tone={player.gender === "Female" ? "orange" : "neutral"}>
                          {genderLabel(player.gender)}
                        </Badge>
                        {player.isInsideSharedSlot && <Badge tone="violet">Slot thay phiên</Badge>}
                      </div>
                    </div>
                    <ScorePill score={player.score} />
                  </article>
                ))}
              </div>
            </div>

            <div className="tool-panel desktop-setup-panel">
              <div className="card-title-row">
                <div>
                  <h2>Shared slots trong DB</h2>
                  <p className="muted">Shared slot counts as one draft slot.</p>
                </div>
                <Badge tone="violet">{sharedSlots.length} slot</Badge>
              </div>
              <div className="chip-grid">
                {availableForShared.map((player) => (
                  <button
                    className={
                      selectedSharedIds.includes(player.id) ? "select-chip active" : "select-chip"
                    }
                    key={player.id}
                    type="button"
                    onClick={() => toggleSharedPlayer(player.id)}
                  >
                    {player.displayName}
                    <small>{player.score}</small>
                  </button>
                ))}
              </div>
              <button
                className="button-primary"
                type="button"
                onClick={createSharedSlot}
                disabled={!canEditRoster || selectedSharedIds.length < 2 || isBusy}
              >
                <Plus size={18} aria-hidden="true" />
                Lưu slot thay phiên
              </button>
              <div className="card-grid">
                {sharedSlots.map((slot) => (
                  <article className="feature-card" key={slot.id}>
                    <div className="card-title-row">
                      <h3>{slot.displayName}</h3>
                      <div className="admin-card-actions">
                        <ScorePill score={slot.averageScore} label="avg" />
                        <button
                          className="icon-button danger"
                          type="button"
                          onClick={() => deleteSharedSlot(slot)}
                          disabled={!canEditRoster || isBusy}
                          title="Xoa shared slot"
                        >
                          <Trash2 size={16} aria-hidden="true" />
                        </button>
                      </div>
                    </div>
                    <div className="badge-row">
                      <Badge tone="violet">Shared</Badge>
                      <Badge tone="sky">{roleLabel(slot.role)}</Badge>
                    </div>
                  </article>
                ))}
              </div>
            </div>

            <div className="tool-panel">
              <div className="card-title-row">
                <div>
                  <h2>Captain selection</h2>
                  <p className="muted">Captains are session players, not separate logins.</p>
                </div>
                {captains && <Badge tone="orange">{captains.balance.status}</Badge>}
              </div>

              <div className="action-row">
                <button
                  className="button-secondary"
                  type="button"
                  onClick={autoSelectCaptains}
                  disabled={isBusy || players.length < 3}
                >
                  Auto balanced captains
                </button>
              </div>

              <div className="form-grid">
                {[0, 1, 2].map((index) => (
                  <label className="field" key={index}>
                    <span>Team {String.fromCharCode(65 + index)}</span>
                    <select
                      className="input"
                      value={manualCaptainIds[index] ?? ""}
                      onChange={(event) => {
                        const nextIds = [...manualCaptainIds];
                        nextIds[index] = event.target.value;
                        setManualCaptainIds(nextIds);
                      }}
                    >
                      <option value="">Chọn đại diện</option>
                      {players
                        .filter((player) => player.isCaptainEligible && !player.isInsideSharedSlot)
                        .map((player) => (
                          <option key={player.id} value={player.id}>
                            {player.displayName} - {player.score}
                          </option>
                        ))}
                    </select>
                  </label>
                ))}
              </div>
              <button className="button-primary" type="button" onClick={saveManualCaptains}>
                <Save size={18} aria-hidden="true" />
                Lưu captain override
              </button>

              {captains && (
                <div className="captain-grid">
                  {captains.captains.map((captain) => (
                    <article className="captain-card" key={captain.teamId}>
                      <small>{captain.teamName}</small>
                      <strong>{captain.displayName}</strong>
                      <ScorePill score={captain.score} />
                    </article>
                  ))}
                </div>
              )}
            </div>

            <div className="draft-frame screen-frame">
              <div className="screen-stack draft-screen">
                <div className="screen-heading inverted">
                  <div>
                    <h1>Khui túi trên cùng một điện thoại</h1>
                  </div>
                  <div className="heading-icon">
                    <Gift size={24} aria-hidden="true" />
                  </div>
                </div>

                <div className="action-row">
                  <button
                    className="button-primary"
                    type="button"
                    onClick={startDraft}
                    disabled={isBusy || session.status === "Drafting"}
                  >
                    Bắt đầu draft
                  </button>
                  <button className="button-secondary" type="button" onClick={refreshDraftState}>
                    Refresh draft-state
                  </button>
                </div>

                {draftState && (
                  <>
                    <div className="draft-status">
                      <div>
                        <span>Trạng thái</span>
                        <strong>{draftState.sessionStatus}</strong>
                      </div>
                      <div>
                        <span>Vòng</span>
                        <strong>
                          {draftState.currentRound ?? "-"} / {draftState.totalRounds}
                        </strong>
                      </div>
                      <div>
                        <span>Lượt hiện tại</span>
                        <strong>{draftState.currentTeam?.name ?? "-"}</strong>
                      </div>
                      <div>
                        <span>Đại diện</span>
                        <strong>{draftState.currentCaptain?.name ?? "-"}</strong>
                      </div>
                    </div>

                    {draftState.message && (
                      <div className="draft-instruction">
                        <UsersRound size={20} aria-hidden="true" />
                        <span>{draftState.message}</span>
                      </div>
                    )}

                    <div className="bag-grid">
                      {draftState.bags.map((bag, index) => (
                        <BlindBagCard
                          key={bag.id}
                          bagNumber={index + 1}
                          isOpened={bag.isOpened}
                          isDisabled={!draftState.viewer.canOpenBag || isBusy || Boolean(pendingReveal)}
                          revealedName={bag.revealedSlot?.displayName}
                          revealedRole={bag.revealedSlot ? roleLabel(bag.revealedSlot.role) : undefined}
                          revealedScore={bag.revealedSlot?.averageScore}
                          onOpen={() => prepareReveal(bag.id)}
                        />
                      ))}
                    </div>

                    {draftState.lastOpenedBag && (
                      <div className="reveal-card">
                        <Badge tone="orange">Đã khui được</Badge>
                        <p>{draftState.lastOpenedBag.message}</p>
                      </div>
                    )}

                    <div className="team-preview-grid">
                      {draftState.teamPreview.map((team) => (
                        <article className="team-preview" key={team.teamId}>
                          <div className="card-title-row">
                            <div>
                              <h3>{team.teamName}</h3>
                              <p>Captain: {team.captainName ?? "-"}</p>
                            </div>
                            <strong>{team.slots.length}/6</strong>
                          </div>
                          <div className="member-line">
                            {team.slots.map((slot) => slot.displayName).join(", ")}
                          </div>
                        </article>
                      ))}
                    </div>
                  </>
                )}

                {!draftReady && !draftState && (
                  <p className="muted text-slate-300">
                    Chọn captains rồi bắt đầu draft để tạo rounds, bags và turns trong DB.
                  </p>
                )}
              </div>
            </div>
          </>
        )}
      </div>

      <ShootingStarRevealModal
        isOpen={Boolean(pendingReveal)}
        rarity={pendingReveal?.rarity ?? "blue"}
        revealedSlot={pendingReveal?.revealedSlot ?? null}
        captainName={pendingReveal?.captainName ?? draftState?.currentCaptain?.name ?? "Đại diện"}
        teamName={pendingReveal?.teamName ?? draftState?.currentTeam?.name ?? "Team"}
        onContinue={continueReveal}
      />
    </section>
  );
}
