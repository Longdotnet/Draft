import { FormEvent, useEffect, useMemo, useState } from "react";
import { Camera, Check, Gift, GripVertical, History, LogOut, Pencil, Plus, RefreshCw, RotateCcw, Save, ShieldCheck, Trash2, UsersRound, X } from "lucide-react";
import {
  apiFetch,
  ApiRequestError,
  type AdminSessionSummaryResponse,
  type AuthResponse,
  type AuthUser,
  type CaptainsResponse,
  type DeleteResponse,
  type DbGender,
  type DbLevel,
  type DbRole,
  type DraftSlotType,
  type DraftBoardAssignmentRequest,
  type DraftSnapshotResponse,
  type DraftStateResponse,
  type OpenBagResponse,
  type PagedResponse,
  type PrepareRevealResponse,
  type SessionPlayerResponse,
  type SessionResponse,
  type SessionStatus,
  type SharedSlotResponse,
  type TeamPreferenceGroupResponse,
  type ZaloBotActionHistoryResponse,
} from "../api/dbClient";
import { BlindBagCard } from "./draft/BlindBagCard";
import { ShootingStarRevealModal } from "./draft/ShootingStarRevealModal";
import { Badge, ScorePill } from "./ui";
import { getRevealRarity, type RevealRarity } from "../lib/revealRarity";
import { ZaloPollImportPanel } from "./ZaloPollImportPanel";

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
  { value: "Unknown", label: "Chưa xác định" },
  { value: "Male", label: "Nam" },
  { value: "Female", label: "Nữ" },
];

const samplePlayers: Array<{ displayName: string; role: DbRole; level: DbLevel; gender: DbGender }> = [
  { displayName: "Nick Tran", role: "Defense", level: "New", gender: "Male" },
  { displayName: "Đặng Thế Nguyễn", role: "Setter", level: "Average", gender: "Male" },
  { displayName: "Thanh Trúc", role: "Defense", level: "New", gender: "Female" },
  { displayName: "Longg", role: "FullStack", level: "Average", gender: "Male" },
  { displayName: "Bảo", role: "New", level: "New", gender: "Male" },
  { displayName: "Bình", role: "Attack", level: "Good", gender: "Male" },
  { displayName: "Nam", role: "Defense", level: "Average", gender: "Male" },
  { displayName: "Anh Duy", role: "Attack", level: "Average", gender: "Male" },
  { displayName: "Tô An", role: "FullStack", level: "Average", gender: "Male" },
  { displayName: "Duy Nam", role: "Attack", level: "Good", gender: "Male" },
  { displayName: "Vinh", role: "Defense", level: "New", gender: "Male" },
  { displayName: "Nghuy", role: "Attack", level: "Good", gender: "Male" },
  { displayName: "Minh Nam", role: "Defense", level: "New", gender: "Male" },
  { displayName: "Quỳnh Mai", role: "Defense", level: "Average", gender: "Female" },
  { displayName: "Phương Duy Đỗ", role: "FullStack", level: "Average", gender: "Male" },
  { displayName: "Hồ Quang Tùng", role: "Attack", level: "Good", gender: "Male" },
  { displayName: "Vivian", role: "Defense", level: "New", gender: "Female" },
  { displayName: "Nguyễn Trí Nhân", role: "Defense", level: "New", gender: "Male" },
  { displayName: "Cẩm Thế", role: "Defense", level: "New", gender: "Female" },
];

const roleLabel = (role: DbRole) =>
  roleOptions.find((option) => option.value === role)?.label ?? role;

const levelLabel = (level: DbLevel) =>
  levelOptions.find((option) => option.value === level)?.label ?? level;

const genderLabel = (gender: DbGender) =>
  genderOptions.find((option) => option.value === gender)?.label ?? gender;

const toRevealSlotType = (type: DraftSlotType) => (type === "Shared" ? "shared" : "single");
const adminSessionPageSize = 5;

const statusLabels: Record<SessionStatus, string> = {
  Setup: "Đang xếp",
  CaptainSelection: "Đã chọn captain",
  Drafting: "Đang bốc túi",
  Finished: "Đã hoàn tất",
  Cancelled: "Đã hủy",
};

function formatAdminSessionDate(value: string) {
  return new Intl.DateTimeFormat("vi-VN", {
    day: "2-digit",
    month: "2-digit",
    year: "numeric",
    hour: "2-digit",
    minute: "2-digit",
  }).format(new Date(value));
}

type PaginationControlsProps = {
  page: number;
  totalPages: number;
  onPageChange: (page: number) => void;
};

function PaginationControls({ page, totalPages, onPageChange }: PaginationControlsProps) {
  if (totalPages <= 1) {
    return null;
  }

  return (
    <div className="pagination-row">
      <button
        className="button-secondary"
        type="button"
        disabled={page <= 1}
        onClick={() => onPageChange(page - 1)}
      >
        Trước
      </button>
      <span>
        {page} / {totalPages}
      </span>
      <button
        className="button-secondary"
        type="button"
        disabled={page >= totalPages}
        onClick={() => onPageChange(page + 1)}
      >
        Sau
      </button>
    </div>
  );
}

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
  const [sessionPage, setSessionPage] = useState(1);
  const [savedSessions, setSavedSessions] =
    useState<PagedResponse<AdminSessionSummaryResponse> | null>(null);
  const [players, setPlayers] = useState<SessionPlayerResponse[]>([]);
  const [sharedSlots, setSharedSlots] = useState<SharedSlotResponse[]>([]);
  const [teamPreferenceGroups, setTeamPreferenceGroups] = useState<TeamPreferenceGroupResponse[]>([]);
  const [captains, setCaptains] = useState<CaptainsResponse | null>(null);
  const [draftState, setDraftState] = useState<DraftStateResponse | null>(null);
  const [manualCaptainIds, setManualCaptainIds] = useState<string[]>(["", "", ""]);
  const [selectedSharedIds, setSelectedSharedIds] = useState<string[]>([]);
  const [selectedTeamPreferenceIds, setSelectedTeamPreferenceIds] = useState<string[]>([]);
  const [playerForm, setPlayerForm] = useState({
    displayName: "",
    role: "Attack" as DbRole,
    level: "Average" as DbLevel,
    gender: "Unknown" as DbGender,
  });
  const [editingPlayerId, setEditingPlayerId] = useState<string | null>(null);
  const [playerEditForm, setPlayerEditForm] = useState({
    displayName: "",
    role: "Attack" as DbRole,
    level: "Average" as DbLevel,
    gender: "Unknown" as DbGender,
    isPresent: true,
    isCaptainEligible: true,
  });
  const [statusMessage, setStatusMessage] = useState<string | null>(null);
  const [error, setError] = useState<string | null>(null);
  const [isBusy, setIsBusy] = useState(false);
  const [pendingReveal, setPendingReveal] = useState<PendingDbReveal | null>(null);
  const [boardAssignments, setBoardAssignments] = useState<Record<string, string>>({});
  const [draggedSlotId, setDraggedSlotId] = useState<string | null>(null);
  const [selectedBoardSlotId, setSelectedBoardSlotId] = useState<string | null>(null);
  const [draftSnapshots, setDraftSnapshots] = useState<PagedResponse<DraftSnapshotResponse> | null>(null);
  const [snapshotPage, setSnapshotPage] = useState(1);
  const [snapshotName, setSnapshotName] = useState("");

  const availableForShared = useMemo(
    () => players.filter((player) => !player.isInsideSharedSlot),
    [players],
  );
  const teamPreferencePlayerIds = useMemo(
    () => new Set(teamPreferenceGroups.flatMap((group) => group.sessionPlayerIds)),
    [teamPreferenceGroups],
  );
  const availableForTeamPreference = useMemo(
    () => players.filter((player) => !teamPreferencePlayerIds.has(player.id)),
    [players, teamPreferencePlayerIds],
  );
  const draftReady = draftState?.sessionStatus === "Drafting";
  const captainsReady = (captains?.captains.length ?? 0) === (session?.teamCount ?? 3);
  const canEditRoster = session?.status !== "Drafting" && session?.status !== "Finished";
  const originalBoardAssignments = useMemo(() => {
    const assignments: Record<string, string> = {};
    draftState?.teamPreview.forEach((team) => {
      team.slots.forEach((slot) => {
        assignments[slot.id] = team.teamId;
      });
    });
    return assignments;
  }, [draftState]);
  const boardHasChanges = useMemo(
    () => Object.entries(originalBoardAssignments).some(
      ([slotId, teamId]) => boardAssignments[slotId] !== teamId,
    ),
    [boardAssignments, originalBoardAssignments],
  );
  const boardCountsAreValid = useMemo(() =>
    (draftState?.teamPreview ?? []).every((team) => {
      const expected = team.slots.length;
      const actual = Object.values(boardAssignments).filter((teamId) => teamId === team.teamId).length;
      return expected === actual;
    }), [boardAssignments, draftState]);

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
    if (draftState?.sessionStatus !== "Finished") {
      setBoardAssignments({});
      setSelectedBoardSlotId(null);
      setDraftSnapshots(null);
      return;
    }
    setBoardAssignments(originalBoardAssignments);
    setSelectedBoardSlotId(null);
    if (token && session) {
      void loadDraftSnapshots(1);
    }
  }, [draftState?.stateToken, draftState?.sessionStatus, session?.id, token]);

  useEffect(() => {
    if (!token || !user) {
      return;
    }

    void loadSessionPage(sessionPage, !session);
  }, [token, user, sessionPage]);

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
    setSavedSessions(null);
    setSessionPage(1);
    setPlayers([]);
    setSharedSlots([]);
    setTeamPreferenceGroups([]);
    setCaptains(null);
    setDraftState(null);
    setSelectedTeamPreferenceIds([]);
    setBoardAssignments({});
    setSelectedBoardSlotId(null);
    setDraftSnapshots(null);
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
      setTeamPreferenceGroups([]);
      setCaptains(null);
      setDraftState(null);
      setSessionPage(1);
      await loadSessionPage(1);
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
      await loadSessionPage(sessionPage);
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
      const nextPage =
        savedSessions && savedSessions.items.length === 1 && sessionPage > 1
          ? sessionPage - 1
          : sessionPage;
      clearSessionData();
      setSessionPage(nextPage);
      setStatusMessage(response.message);
      await loadSessionPage(nextPage, true);
      return response;
    });
  }

  function clearSessionData() {
    setSession(null);
    setPlayers([]);
    setSharedSlots([]);
    setTeamPreferenceGroups([]);
    setCaptains(null);
    setDraftState(null);
    setManualCaptainIds(["", "", ""]);
    setSelectedSharedIds([]);
    setSelectedTeamPreferenceIds([]);
    setEditingPlayerId(null);
    setPendingReveal(null);
    setBoardAssignments({});
    setSelectedBoardSlotId(null);
    setDraftSnapshots(null);
    setSnapshotPage(1);
    setSnapshotName("");
  }

  async function refreshSessionData(targetSession: Pick<SessionResponse, "id"> | null = session) {
    if (!token || !targetSession) return;
    const [freshSession, freshPlayers, freshSharedSlots, freshTeamPreferenceGroups] = await Promise.all([
      apiFetch<SessionResponse>(`/sessions/${targetSession.id}`, { token }),
      apiFetch<SessionPlayerResponse[]>(`/sessions/${targetSession.id}/players`, { token }),
      apiFetch<SharedSlotResponse[]>(`/sessions/${targetSession.id}/shared-slots`, { token }),
      apiFetch<TeamPreferenceGroupResponse[]>(`/sessions/${targetSession.id}/team-preferences`, { token }),
    ]);
    setSession(freshSession);
    setSessionName(freshSession.name);
    setPlayers(freshPlayers);
    setSharedSlots(freshSharedSlots);
    setTeamPreferenceGroups(freshTeamPreferenceGroups);
    return freshSession;
  }

  async function loadSessionRuntimeState(targetSession: SessionResponse) {
    if (!token) return;

    try {
      const captainResponse = await apiFetch<CaptainsResponse>(
        `/sessions/${targetSession.id}/captains`,
        { token },
      );
      setCaptains(captainResponse);
      setManualCaptainIds(captainResponse.captains.map((captain) => captain.sessionPlayerId));
    } catch {
      setCaptains(null);
      setManualCaptainIds(["", "", ""]);
    }

    if (targetSession.status === "Drafting" || targetSession.status === "Finished") {
      const state = await apiFetch<DraftStateResponse>(`/sessions/${targetSession.id}/draft-state`, {
        token,
      });
      setDraftState(state);
    } else {
      setDraftState(null);
    }
  }

  async function selectSession(sessionId: string) {
    if (!token) return;

    await runAction(async () => {
      setStatusMessage(null);
      setError(null);
      setSelectedSharedIds([]);
      setSelectedTeamPreferenceIds([]);
      setEditingPlayerId(null);
      setPendingReveal(null);

      const freshSession = await refreshSessionData({ id: sessionId });
      if (freshSession) {
        await loadSessionRuntimeState(freshSession);
      }

      return freshSession;
    });
  }

  async function loadSessionPage(page: number, autoSelectFirst = false) {
    if (!token) return;

    try {
      const response = await apiFetch<PagedResponse<AdminSessionSummaryResponse>>(
        `/sessions?page=${page}&pageSize=${adminSessionPageSize}`,
        { token },
      );
      setSavedSessions(response);

      if (autoSelectFirst && response.items[0]) {
        await selectSession(response.items[0].id);
      }

      if (!response.items.length && page > 1) {
        setSessionPage(page - 1);
      }
    } catch (caught) {
      setError(caught instanceof Error ? caught.message : "Không tải được danh sách buổi thi đấu.");
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
      setPlayerForm({ displayName: "", role: "Attack", level: "Average", gender: "Unknown" });
      await refreshSessionData();
      await loadSessionPage(sessionPage);
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
      await loadSessionPage(sessionPage);
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
      await loadSessionPage(sessionPage);
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
      await loadSessionPage(sessionPage);
    }, "Đã seed 19 người chơi và slot Bảo / Bình vào database.");
  }

  function toggleSharedPlayer(playerId: string) {
    setSelectedSharedIds((current) =>
      current.includes(playerId)
        ? current.filter((id) => id !== playerId)
        : [...current, playerId],
    );
  }

  function toggleTeamPreferencePlayer(playerId: string) {
    setSelectedTeamPreferenceIds((current) =>
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
    const confirmed = window.confirm(`Xóa shared slot "${slot.displayName}"?`);
    if (!confirmed) return;

    await runAction(async () => {
      const response = await apiFetch<DeleteResponse>(
        `/sessions/${session.id}/shared-slots/${slot.id}`,
        { method: "DELETE", token },
      );
      await refreshSessionData();
      return response;
    }, "Đã xóa shared slot.");
  }

  async function createTeamPreferenceGroup() {
    if (!token || !session || selectedTeamPreferenceIds.length < 2) return;
    await runAction(async () => {
      await apiFetch<TeamPreferenceGroupResponse>(`/sessions/${session.id}/team-preferences`, {
        method: "POST",
        token,
        body: { sessionPlayerIds: selectedTeamPreferenceIds },
      });
      setSelectedTeamPreferenceIds([]);
      await refreshSessionData();
    }, "Đã lưu nhóm muốn chung team.");
  }

  async function deleteTeamPreferenceGroup(group: TeamPreferenceGroupResponse) {
    if (!token || !session) return;
    const confirmed = window.confirm(`Xóa nhóm chung team "${group.playerNames.join(" / ")}"?`);
    if (!confirmed) return;

    await runAction(async () => {
      const response = await apiFetch<DeleteResponse>(
        `/sessions/${session.id}/team-preferences/${group.id}`,
        { method: "DELETE", token },
      );
      await refreshSessionData();
      return response;
    }, "Đã xóa nhóm muốn chung team.");
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
      await loadSessionPage(sessionPage);
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
      await loadSessionPage(sessionPage);
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
      await loadSessionPage(sessionPage);
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

  async function loadDraftSnapshots(page = snapshotPage) {
    if (!token || !session) return;
    try {
      const response = await apiFetch<PagedResponse<DraftSnapshotResponse>>(
        `/sessions/${session.id}/draft-snapshots?page=${page}&pageSize=6`,
        { token },
      );
      setDraftSnapshots(response);
      setSnapshotPage(response.page);
    } catch (caught) {
      setError(caught instanceof Error ? caught.message : "Không tải được snapshot đội hình.");
    }
  }

  function moveBoardSlot(slotId: string, targetTeamId: string) {
    const slot = draftState?.teamPreview.flatMap((team) => team.slots).find((item) => item.id === slotId);
    if (!slot || slot.isCaptainSlot || boardAssignments[slotId] === targetTeamId) return;
    setBoardAssignments((current) => ({ ...current, [slotId]: targetTeamId }));
    setSelectedBoardSlotId(null);
  }

  function resetDraftBoardEditor() {
    setBoardAssignments(originalBoardAssignments);
    setSelectedBoardSlotId(null);
    setDraggedSlotId(null);
  }

  async function saveDraftBoard() {
    if (!token || !session || !draftState || !boardHasChanges || !boardCountsAreValid) return;
    const assignments: DraftBoardAssignmentRequest[] = draftState.teamPreview.flatMap((team) =>
      team.slots.map((slot) => ({
        slotId: slot.id,
        expectedTeamId: team.teamId,
        targetTeamId: boardAssignments[slot.id] ?? team.teamId,
      })),
    );
    await runAction(async () => {
      const response = await apiFetch<DraftStateResponse>(`/sessions/${session.id}/draft-board`, {
        method: "PUT",
        token,
        body: { expectedStateToken: draftState.stateToken, assignments },
      });
      setDraftState(response);
      await refreshSessionData();
      await loadSessionPage(sessionPage);
      return response;
    }, "Đã lưu đội hình mới. Toàn bộ lần chỉnh này được ghi thành một thao tác.");
  }

  async function createDraftSnapshot() {
    if (!token || !session || draftState?.sessionStatus !== "Finished") return;
    await runAction(async () => {
      const response = await apiFetch<DraftSnapshotResponse>(`/sessions/${session.id}/draft-snapshots`, {
        method: "POST",
        token,
        body: { name: snapshotName.trim() || null },
      });
      setSnapshotName("");
      await loadDraftSnapshots(1);
      return response;
    }, "Đã lưu snapshot đội hình hiện tại.");
  }

  async function restoreDraftSnapshot(snapshot: DraftSnapshotResponse) {
    if (!token || !session || !draftState) return;
    const confirmed = window.confirm(
      `Khôi phục snapshot “${snapshot.name}”? Đội hình và captain hiện tại sẽ được thay thế. Lịch nhắc và cấu hình bot không bị đổi.`,
    );
    if (!confirmed) return;
    await runAction(async () => {
      const response = await apiFetch<DraftStateResponse>(
        `/sessions/${session.id}/draft-snapshots/${snapshot.id}/restore`,
        {
          method: "POST",
          token,
          body: { expectedStateToken: draftState.stateToken },
        },
      );
      setDraftState(response);
      await refreshSessionData();
      await loadSessionPage(sessionPage);
      return response;
    }, `Đã khôi phục snapshot “${snapshot.name}”. Bạn vẫn có thể undo thao tác này.`);
  }

  async function deleteDraftSnapshot(snapshot: DraftSnapshotResponse) {
    if (!token || !session || !window.confirm(`Xoá snapshot “${snapshot.name}”?`)) return;
    await runAction(async () => {
      const response = await apiFetch<DeleteResponse>(
        `/sessions/${session.id}/draft-snapshots/${snapshot.id}`,
        { method: "DELETE", token },
      );
      const nextPage = draftSnapshots?.items.length === 1 && snapshotPage > 1
        ? snapshotPage - 1
        : snapshotPage;
      await loadDraftSnapshots(nextPage);
      return response;
    }, "Đã xoá snapshot.");
  }

  async function undoLatestBackendAction() {
    if (!token || !session) return;
    const response = await runAction(async () => {
      const history = await apiFetch<ZaloBotActionHistoryResponse[]>(
        `/sessions/${session.id}/action-history?count=20`,
        { token },
      );
      const latest = history.find((action) => action.isUndoable && !action.undoneAt);
      if (!latest) throw new Error("Hiện không có thao tác mới nhất nào có thể hoàn tác.");
      if (!window.confirm(`Hoàn tác thao tác mới nhất: “${latest.summary}”?`)) return null;
      await apiFetch<ZaloBotActionHistoryResponse>(
        `/sessions/${session.id}/action-history/${latest.id}/undo`,
        { method: "POST", token },
      );
      const response = await apiFetch<DraftStateResponse>(`/sessions/${session.id}/draft-state`, { token });
      setDraftState(response);
      await refreshSessionData();
      await loadSessionPage(sessionPage);
      return response;
    });
    if (response) setStatusMessage("Đã hoàn tác thay đổi backend gần nhất.");
  }

  async function undoLastDraftPick() {
    if (!token || !session) return;

    await runAction(async () => {
      const response = await apiFetch<DraftStateResponse>(`/sessions/${session.id}/undo-last-pick`, {
        method: "POST",
        token,
      });
      setDraftState(response);
      await refreshSessionData();
      await loadSessionPage(sessionPage);
      return response;
    }, "Đã quay lại lượt bốc gần nhất.");
  }

  async function resetDraftFromStart() {
    if (!token || !session) return;
    const confirmed = window.confirm(
      "Reset toàn bộ draft từ đầu? Tất cả túi đã khui và kết quả chia đội hiện tại sẽ bị xóa, sau đó app random lại rounds/bags/turns.",
    );
    if (!confirmed) return;

    await runAction(async () => {
      const response = await apiFetch<DraftStateResponse>(`/sessions/${session.id}/reset-draft`, {
        method: "POST",
        token,
      });
      setPendingReveal(null);
      setDraftState(response);
      await refreshSessionData();
      await loadSessionPage(sessionPage);
      return response;
    }, "Đã reset draft và random lại từ đầu.");
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
      await loadSessionPage(sessionPage);
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
              <h1>Đăng nhập</h1>
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

          <div className="admin-session-browser">
            <div className="card-title-row">
              <div>
                <h3>Buổi thi đấu đã lưu</h3>
                <p className="muted">Chọn lại buổi đang làm dở hoặc xem kết quả buổi đã draft.</p>
              </div>
              <Badge tone="neutral">{savedSessions?.totalItems ?? 0} buổi</Badge>
            </div>

            {savedSessions?.items.length ? (
              <div className="admin-session-list">
                {savedSessions.items.map((item) => {
                  const isActive = session?.id === item.id;
                  const isRosterReady =
                    item.playerCount >= item.requiredPlayerCount &&
                    item.playerCount % item.teamCount === 0;

                  return (
                    <button
                      className={["admin-session-card", isActive ? "active" : ""].join(" ")}
                      key={item.id}
                      type="button"
                      onClick={() => selectSession(item.id)}
                    >
                      <div>
                        <strong>{item.name}</strong>
                        <small>Cập nhật {formatAdminSessionDate(item.updatedAt)}</small>
                      </div>
                      <div className="badge-row">
                        <Badge tone={item.status === "Drafting" ? "orange" : "sky"}>
                          {statusLabels[item.status]}
                        </Badge>
                        <Badge tone={isRosterReady ? "sky" : "orange"}>
                          {item.playerCount}/{item.requiredPlayerCount} người
                        </Badge>
                        {isActive && <Badge tone="neutral">Đang mở</Badge>}
                      </div>
                    </button>
                  );
                })}
              </div>
            ) : (
              <div className="notice notice-soft">Chưa có buổi thi đấu nào được lưu.</div>
            )}

            <PaginationControls
              page={savedSessions?.page ?? sessionPage}
              totalPages={savedSessions?.totalPages ?? 0}
              onPageChange={setSessionPage}
            />
          </div>
        </div>

        {session && (
          <>
            <ZaloPollImportPanel
              token={token}
              session={session}
              onSessionUpdated={(updatedSession) => {
                setSession(updatedSession);
                setSessionName(updatedSession.name);
              }}
              onImported={async () => {
                await refreshSessionData();
                await loadSessionPage(sessionPage);
              }}
            />

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
                          title="Xóa shared slot"
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

            <div className="tool-panel desktop-setup-panel">
              <div className="card-title-row">
                <div>
                  <h2>Muốn chung team</h2>
                  <p className="muted">
                    Dàn xếp nhẹ: nếu một người trong nhóm được bóc túi, các người còn lại sẽ vào cùng team.
                  </p>
                </div>
                <Badge tone="orange">{teamPreferenceGroups.length} nhóm</Badge>
              </div>
              <div className="chip-grid">
                {availableForTeamPreference.map((player) => (
                  <button
                    className={
                      selectedTeamPreferenceIds.includes(player.id) ? "select-chip active" : "select-chip"
                    }
                    key={player.id}
                    type="button"
                    onClick={() => toggleTeamPreferencePlayer(player.id)}
                  >
                    {player.displayName}
                    <small>{player.score}</small>
                  </button>
                ))}
              </div>
              <button
                className="button-primary"
                type="button"
                onClick={createTeamPreferenceGroup}
                disabled={!canEditRoster || selectedTeamPreferenceIds.length < 2 || isBusy}
              >
                <Plus size={18} aria-hidden="true" />
                Lưu nhóm chung team
              </button>
              <div className="card-grid">
                {teamPreferenceGroups.map((group) => (
                  <article className="feature-card" key={group.id}>
                    <div className="card-title-row">
                      <h3>{group.playerNames.join(" / ")}</h3>
                      <div className="admin-card-actions">
                        <ScorePill score={group.averageScore} label="avg" />
                        <button
                          className="icon-button danger"
                          type="button"
                          onClick={() => deleteTeamPreferenceGroup(group)}
                          disabled={!canEditRoster || isBusy}
                          title="Xóa nhóm chung team"
                        >
                          <Trash2 size={16} aria-hidden="true" />
                        </button>
                      </div>
                    </div>
                    <div className="badge-row">
                      <Badge tone="orange">Chung team</Badge>
                      <Badge tone="neutral">{group.playerNames.length} người</Badge>
                    </div>
                  </article>
                ))}
              </div>
            </div>

            <div className="tool-panel">
              <div className="card-title-row">
                <div>
                  <h2>Captain selection</h2>
                  <p className="muted">
                    Người trong slot thay phiên hoặc nhóm chung team vẫn có thể làm captain.
                  </p>
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
                        .filter((player) => player.isPresent && (player.isCaptainEligible || player.isInsideSharedSlot))
                        .map((player) => (
                          <option key={player.id} value={player.id}>
                            {player.displayName} - {player.score}
                            {player.isInsideSharedSlot ? " · slot thay phiên" : ""}
                            {teamPreferencePlayerIds.has(player.id) ? " · chung team" : ""}
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
                  <button
                    className="button-secondary"
                    type="button"
                    onClick={undoLastDraftPick}
                    disabled={isBusy || !draftState?.lastOpenedBag}
                  >
                    <RotateCcw size={17} aria-hidden="true" />
                    Quay lại lượt vừa khui
                  </button>
                  <button
                    className="button-danger"
                    type="button"
                    onClick={resetDraftFromStart}
                    disabled={isBusy || !captainsReady}
                  >
                    <RefreshCw size={17} aria-hidden="true" />
                    Reset draft từ đầu
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

                    {draftState.sessionStatus === "Finished" ? (
                      <section className="draft-board-editor" aria-label="Chỉnh đội hình sau draft">
                        <div className="draft-board-heading">
                          <div>
                            <h2>Chỉnh đội hình</h2>
                            <p>
                              Kéo slot sang team khác, hoặc chạm một slot rồi chọn team đích. Slot share luôn đi cùng nhau.
                            </p>
                          </div>
                          <Badge tone={boardCountsAreValid ? "good" : "warn"}>
                            {boardCountsAreValid ? "Đủ slot" : "Cần chuyển bù slot"}
                          </Badge>
                        </div>

                        <div className="draft-board-grid">
                          {draftState.teamPreview.map((team) => {
                            const slots = draftState.teamPreview
                              .flatMap((item) => item.slots)
                              .filter((slot) => boardAssignments[slot.id] === team.teamId);
                            const score = slots.reduce((total, slot) => total + slot.averageScore, 0);
                            return (
                              <article
                                className="draft-board-team"
                                key={team.teamId}
                                onDragOver={(event) => event.preventDefault()}
                                onDrop={(event) => {
                                  event.preventDefault();
                                  if (draggedSlotId) moveBoardSlot(draggedSlotId, team.teamId);
                                  setDraggedSlotId(null);
                                }}
                              >
                                <div className="draft-board-team-heading">
                                  <div>
                                    <h3>{team.teamName}</h3>
                                    <p>Đội trưởng: {team.captainName ?? "-"}</p>
                                  </div>
                                  <div className="draft-board-metrics">
                                    <strong>{slots.length}/{team.slots.length}</strong>
                                    <span>{score.toFixed(1)} điểm</span>
                                  </div>
                                </div>

                                {selectedBoardSlotId && boardAssignments[selectedBoardSlotId] !== team.teamId && (
                                  <button
                                    className="draft-board-target"
                                    type="button"
                                    onClick={() => moveBoardSlot(selectedBoardSlotId, team.teamId)}
                                  >
                                    <Check size={16} aria-hidden="true" />
                                    Chuyển slot đang chọn vào {team.teamName}
                                  </button>
                                )}

                                <div className="draft-board-slots">
                                  {slots.map((slot) => (
                                    <button
                                      className={`draft-board-slot${selectedBoardSlotId === slot.id ? " selected" : ""}${slot.isCaptainSlot ? " locked" : ""}`}
                                      type="button"
                                      key={slot.id}
                                      draggable={!slot.isCaptainSlot}
                                      onDragStart={() => {
                                        setDraggedSlotId(slot.id);
                                        setSelectedBoardSlotId(slot.id);
                                      }}
                                      onDragEnd={() => setDraggedSlotId(null)}
                                      onClick={() => {
                                        if (!slot.isCaptainSlot) {
                                          setSelectedBoardSlotId((current) => current === slot.id ? null : slot.id);
                                        }
                                      }}
                                    >
                                      <GripVertical size={17} aria-hidden="true" />
                                      <span>
                                        <strong>{slot.displayName}</strong>
                                        <small>
                                          {slot.type === "Shared" ? "Share slot" : "Một người"} · {slot.averageScore.toFixed(1)} điểm
                                        </small>
                                      </span>
                                      {slot.isCaptainSlot && <em>Khoá</em>}
                                    </button>
                                  ))}
                                </div>
                              </article>
                            );
                          })}
                        </div>

                        {!boardCountsAreValid && (
                          <p className="draft-board-warning">
                            Các team chưa cân số slot. Chuyển thêm một slot theo chiều ngược lại rồi mới lưu.
                          </p>
                        )}

                        <div className="action-row draft-board-actions">
                          <button
                            className="button-primary"
                            type="button"
                            disabled={isBusy || !boardHasChanges || !boardCountsAreValid}
                            onClick={saveDraftBoard}
                          >
                            <Save size={17} aria-hidden="true" />
                            Lưu đội hình
                          </button>
                          <button
                            className="button-secondary"
                            type="button"
                            disabled={isBusy || !boardHasChanges}
                            onClick={resetDraftBoardEditor}
                          >
                            <X size={17} aria-hidden="true" />
                            Bỏ thay đổi
                          </button>
                          <button
                            className="button-secondary"
                            type="button"
                            disabled={isBusy}
                            onClick={undoLatestBackendAction}
                          >
                            <History size={17} aria-hidden="true" />
                            Undo thao tác mới nhất
                          </button>
                        </div>

                        <div className="snapshot-panel">
                          <div className="snapshot-create-row">
                            <div>
                              <h3>Snapshot đội hình</h3>
                              <p>Lưu một mốc để quay lại đúng đội hình này khi cần.</p>
                            </div>
                            <input
                              value={snapshotName}
                              maxLength={160}
                              onChange={(event) => setSnapshotName(event.target.value)}
                              placeholder="Tên mốc, ví dụ: Đội hình ưng ý lần 1"
                            />
                            <button
                              className="button-secondary"
                              type="button"
                              disabled={isBusy || boardHasChanges}
                              onClick={createDraftSnapshot}
                              title={boardHasChanges ? "Hãy lưu hoặc bỏ thay đổi trước khi tạo snapshot" : undefined}
                            >
                              <Camera size={17} aria-hidden="true" />
                              Lưu snapshot
                            </button>
                          </div>

                          {draftSnapshots && draftSnapshots.items.length > 0 ? (
                            <div className="snapshot-list">
                              {draftSnapshots.items.map((snapshot) => (
                                <article className="snapshot-item" key={snapshot.id}>
                                  <div>
                                    <strong>{snapshot.name}</strong>
                                    <span>
                                      {formatAdminSessionDate(snapshot.createdAt)} · {snapshot.createdBy}
                                    </span>
                                  </div>
                                  <div className="snapshot-actions">
                                    <button
                                      className="button-secondary"
                                      type="button"
                                      disabled={isBusy || boardHasChanges}
                                      onClick={() => restoreDraftSnapshot(snapshot)}
                                    >
                                      Khôi phục
                                    </button>
                                    <button
                                      className="icon-button danger"
                                      type="button"
                                      disabled={isBusy}
                                      onClick={() => deleteDraftSnapshot(snapshot)}
                                      aria-label={`Xoá snapshot ${snapshot.name}`}
                                    >
                                      <Trash2 size={16} aria-hidden="true" />
                                    </button>
                                  </div>
                                </article>
                              ))}
                              <PaginationControls
                                page={draftSnapshots.page}
                                totalPages={draftSnapshots.totalPages}
                                onPageChange={(page) => void loadDraftSnapshots(page)}
                              />
                            </div>
                          ) : (
                            <p className="muted">Chưa có snapshot nào cho buổi này.</p>
                          )}
                        </div>
                      </section>
                    ) : (
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
                    )}
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
