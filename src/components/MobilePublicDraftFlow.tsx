import { useEffect, useMemo, useState } from "react";
import { Gift, RefreshCw, UsersRound } from "lucide-react";
import {
  apiFetch,
  type CaptainsResponse,
  type DbGender,
  type DbRole,
  type DraftSlotType,
  type DraftStateResponse,
  type OpenBagResponse,
  type PagedResponse,
  type PrepareRevealResponse,
  type PublicSessionSummaryResponse,
  type SessionPlayerResponse,
} from "../api/dbClient";
import { getRevealRarity, type RevealRarity } from "../lib/revealRarity";
import { Badge, ScorePill } from "./ui";
import { BlindBagCard } from "./draft/BlindBagCard";
import { ShootingStarRevealModal } from "./draft/ShootingStarRevealModal";

const sessionPageSize = 3;
const playerPageSize = 6;

const roleLabels: Record<DbRole, string> = {
  Attack: "Tấn công",
  Defense: "Thủ",
  Setter: "Chuyền hai",
  FullStack: "Toàn diện",
  New: "Người mới",
};

const genderLabels: Record<DbGender, string> = {
  Male: "Nam",
  Female: "Nữ",
};

const statusLabels = {
  Setup: "Đang xếp",
  CaptainSelection: "Đã chọn captain",
  Drafting: "Đang bóc túi",
  Finished: "Đã hoàn tất",
  Cancelled: "Đã hủy",
};

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

type PaginationControlsProps = {
  page: number;
  totalPages: number;
  onPageChange: (page: number) => void;
};

const toRevealSlotType = (type: DraftSlotType) => (type === "Shared" ? "shared" : "single");

function formatSessionDate(value: string) {
  return new Intl.DateTimeFormat("vi-VN", {
    day: "2-digit",
    month: "2-digit",
    hour: "2-digit",
    minute: "2-digit",
  }).format(new Date(value));
}

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

export function MobilePublicDraftFlow() {
  const [sessionPage, setSessionPage] = useState(1);
  const [sessions, setSessions] =
    useState<PagedResponse<PublicSessionSummaryResponse> | null>(null);
  const [selectedSession, setSelectedSession] = useState<PublicSessionSummaryResponse | null>(null);
  const [playerPage, setPlayerPage] = useState(1);
  const [players, setPlayers] = useState<PagedResponse<SessionPlayerResponse> | null>(null);
  const [captains, setCaptains] = useState<CaptainsResponse | null>(null);
  const [draftState, setDraftState] = useState<DraftStateResponse | null>(null);
  const [pendingReveal, setPendingReveal] = useState<PendingDbReveal | null>(null);
  const [statusMessage, setStatusMessage] = useState<string | null>(null);
  const [error, setError] = useState<string | null>(null);
  const [isBusy, setIsBusy] = useState(false);

  const requiredPlayerCount = selectedSession?.requiredPlayerCount ?? 18;
  const savedPlayerCount = players?.totalItems ?? selectedSession?.playerCount ?? 0;
  const hasEnoughPlayers = savedPlayerCount >= requiredPlayerCount;
  const captainsReady = (captains?.captains.length ?? 0) === (selectedSession?.teamCount ?? 3);
  const canStartDraft =
    Boolean(selectedSession) &&
    hasEnoughPlayers &&
    captainsReady &&
    selectedSession?.status !== "Drafting" &&
    selectedSession?.status !== "Finished";
  const canShowDraft =
    draftState?.sessionStatus === "Drafting" || draftState?.sessionStatus === "Finished";

  const selectedSessionIds = useMemo(
    () => new Set(sessions?.items.map((session) => session.id) ?? []),
    [sessions],
  );

  useEffect(() => {
    void loadSessions(sessionPage);
  }, [sessionPage]);

  useEffect(() => {
    if (!selectedSession) {
      setPlayers(null);
      setCaptains(null);
      setDraftState(null);
      return;
    }

    void loadSelectedSessionData(selectedSession.id, playerPage);
  }, [selectedSession?.id, playerPage]);

  useEffect(() => {
    if (!selectedSession) {
      return;
    }

    const timer = window.setInterval(() => {
      void refreshSelectedSession();
    }, 5000);

    return () => window.clearInterval(timer);
  }, [selectedSession?.id, sessionPage, playerPage]);

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
      setError(caught instanceof Error ? caught.message : "Có lỗi xảy ra.");
      return null;
    } finally {
      setIsBusy(false);
    }
  }

  async function loadSessions(page: number) {
    try {
      const response = await apiFetch<PagedResponse<PublicSessionSummaryResponse>>(
        `/public/sessions?page=${page}&pageSize=${sessionPageSize}`,
      );
      setSessions(response);
      setSelectedSession((current) => {
        if (current) {
          const refreshedCurrent = response.items.find((session) => session.id === current.id);
          if (refreshedCurrent) {
            return refreshedCurrent;
          }
        }

        return response.items[0] ?? null;
      });
    } catch (caught) {
      setError(caught instanceof Error ? caught.message : "Không tải được session.");
    }
  }

  async function loadSelectedSessionData(sessionId: string, page: number) {
    try {
      const [playerResponse, captainResponse, draftResponse] = await Promise.all([
        apiFetch<PagedResponse<SessionPlayerResponse>>(
          `/public/sessions/${sessionId}/players?page=${page}&pageSize=${playerPageSize}`,
        ),
        apiFetch<CaptainsResponse>(`/public/sessions/${sessionId}/captains`),
        apiFetch<DraftStateResponse>(`/public/sessions/${sessionId}/draft-state`),
      ]);
      setPlayers(playerResponse);
      setCaptains(captainResponse);
      setDraftState(draftResponse);
    } catch (caught) {
      setError(caught instanceof Error ? caught.message : "Không tải được dữ liệu session.");
    }
  }

  async function refreshSelectedSession() {
    if (!selectedSession) return;
    await loadSessions(sessionPage);
    await loadSelectedSessionData(selectedSession.id, playerPage);
  }

  function chooseSession(nextSession: PublicSessionSummaryResponse) {
    setSelectedSession(nextSession);
    setPlayerPage(1);
    setStatusMessage(null);
    setError(null);
  }

  async function autoSelectCaptains() {
    if (!selectedSession) return;

    await runAction(async () => {
      const response = await apiFetch<CaptainsResponse>(
        `/public/sessions/${selectedSession.id}/captains/auto-select`,
        { method: "POST" },
      );
      setCaptains(response);
      await refreshSelectedSession();
      return response;
    }, "Đã random chọn 3 captain cho session này.");
  }

  async function startDraft() {
    if (!selectedSession) return;

    await runAction(async () => {
      const response = await apiFetch<DraftStateResponse>(
        `/public/sessions/${selectedSession.id}/start-draft`,
        { method: "POST" },
      );
      setDraftState(response);
      await refreshSelectedSession();
      return response;
    }, "Đã bắt đầu bóc túi mù. Admin đưa điện thoại cho captain hiện tại.");
  }

  async function prepareReveal(bagId: string) {
    if (!selectedSession || pendingReveal) return;

    await runAction(async () => {
      const response = await apiFetch<PrepareRevealResponse>(
        `/public/sessions/${selectedSession.id}/blind-bags/${bagId}/prepare-reveal`,
        { method: "POST" },
      );

      setPendingReveal({
        bagId,
        rarity: getRevealRarity(response.revealedSlot.averageScore),
        revealedSlot: {
          id: response.revealedSlot.id,
          displayName: response.revealedSlot.displayName,
          role: roleLabels[response.revealedSlot.role],
          gender: genderLabels[response.revealedSlot.gender],
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
    if (!pendingReveal || !selectedSession) return;

    const bagId = pendingReveal.bagId;
    await runAction(async () => {
      const response = await apiFetch<OpenBagResponse>(
        `/public/sessions/${selectedSession.id}/blind-bags/${bagId}/open`,
        { method: "POST" },
      );
      setPendingReveal(null);
      setStatusMessage(null);
      await refreshSelectedSession();
      return response;
    });
  }

  return (
    <section className="screen-frame mobile-public-frame">
      <div className="screen-stack">
        <div className="screen-heading">
          <div>  
            <h1>CLB Bóng chuyền Newbie</h1>   
          </div>
          <div className="heading-icon">
            <Gift size={24} aria-hidden="true" />
          </div>
        </div>

        <div className="tool-panel mobile-session-panel">
          <div className="card-title-row">
            <div>
              <h2>Lịch sân gần nhất</h2>
              <p className="muted">Tự chọn sân mới nhất, có thể đổi sân bên dưới.</p>
            </div>
            <button
              className="icon-button"
              type="button"
              onClick={() =>
                selectedSession ? void refreshSelectedSession() : void loadSessions(sessionPage)
              }
            >
              <RefreshCw size={18} aria-hidden="true" />
            </button>
          </div>

          {sessions?.items.length ? (
            <div className="mobile-session-list">
              {sessions.items.map((item) => (
                <button
                  className={[
                    "mobile-session-card",
                    selectedSession?.id === item.id ? "active" : "",
                  ].join(" ")}
                  key={item.id}
                  type="button"
                  onClick={() => chooseSession(item)}
                >
                  <span>{item.name}</span>
                  <small>{formatSessionDate(item.createdAt)}</small>
                  <strong>
                    {item.playerCount}/{item.requiredPlayerCount} người
                  </strong>
                  <Badge tone={item.status === "Drafting" ? "orange" : "sky"}>
                    {statusLabels[item.status]}
                  </Badge>
                </button>
              ))}
            </div>
          ) : (
            <div className="notice notice-soft">Đang đợi admin tạo buổi thi đấu.</div>
          )}

          <PaginationControls
            page={sessions?.page ?? sessionPage}
            totalPages={sessions?.totalPages ?? 0}
            onPageChange={setSessionPage}
          />
        </div>

        {selectedSession && !selectedSessionIds.has(selectedSession.id) && (
          <div className="notice notice-soft">Buổi thi đấu đã đổi trang, hãy chọn lại buổi thi đấu gần nhất.</div>
        )}

        {selectedSession && !hasEnoughPlayers && (
          <div className="tool-panel mobile-waiting-panel">
            <Badge tone="orange">Đang chờ</Badge>
            <h2>Đang đợi sắp xếp từ Long</h2>
            <p className="screen-copy">
              Long chưa lưu đủ danh sách người tham gia. Hiện có {savedPlayerCount}/
              {requiredPlayerCount} người chơi.
            </p>
          </div>
        )}

        {selectedSession && hasEnoughPlayers && (
          <>
            <div className="tool-panel">
              <div className="card-title-row">
                <div>
                  <h2>Danh sách tham gia</h2>
                  <p className="muted">
                    {savedPlayerCount}/{requiredPlayerCount} người đã đăng ký trên Zalo.
                  </p>
                </div>
                <Badge tone="sky">{statusLabels[selectedSession.status]}</Badge>
              </div>

              <div className="mobile-roster-grid">
                {players?.items.map((player) => (
                  <article className="mini-card" key={player.id}>
                    <div>
                      <h3>{player.displayName}</h3>
                      <div className="badge-row">
                        <Badge tone="sky">{roleLabels[player.role]}</Badge>
                        <Badge tone={player.gender === "Female" ? "orange" : "neutral"}>
                          {genderLabels[player.gender]}
                        </Badge>
                      </div>
                    </div>
                    <ScorePill score={player.score} />
                  </article>
                ))}
              </div>

              <PaginationControls
                page={players?.page ?? playerPage}
                totalPages={players?.totalPages ?? 0}
                onPageChange={setPlayerPage}
              />
            </div>

            <div className="tool-panel">
              <div className="card-title-row">
                <div>
                  <h2>Lựa chọn đội trưởng </h2>
                  <p className="muted">Random đội trưởng từ danh sách người chơi đã đăng ký.</p>
                </div>
                {captainsReady && <Badge tone="orange">Sẵn sàng</Badge>}
              </div>

              {!captainsReady && (
                <button
                  className="button-primary"
                  type="button"
                  disabled={isBusy}
                  onClick={autoSelectCaptains}
                >
                  <UsersRound size={18} aria-hidden="true" />
                  Random chọn captain
                </button>
              )}

              {captainsReady && (
                <div className="captain-grid">
                  {captains?.captains.map((captain) => (
                    <article className="captain-card" key={captain.teamId}>
                      <small>{captain.teamName}</small>
                      <strong>{captain.displayName}</strong>
                      <ScorePill score={captain.score} />
                    </article>
                  ))}
                </div>
              )}

              {canStartDraft && (
                <button
                  className="button-primary"
                  type="button"
                  disabled={isBusy}
                  onClick={startDraft}
                >
                  <Gift size={18} aria-hidden="true" />
                  Bắt đầu bóc túi mù
                </button>
              )}
            </div>

            {canShowDraft && draftState && (
              <div className="draft-frame screen-frame">
                <div className="screen-stack draft-screen">
                  <div className="screen-heading inverted">
                    <div>
                      <p className="eyebrow">One-device draft</p>
                      <h1>Đưa điện thoại cho captain hiện tại</h1>
                    </div>
                    <div className="heading-icon">
                      <Gift size={24} aria-hidden="true" />
                    </div>
                  </div>

                  <div className="draft-status">
                    <div>
                      <span>Vòng</span>
                      <strong>
                        {draftState.currentRound ?? "-"} / {draftState.totalRounds}
                      </strong>
                    </div>
                    <div>
                      <span>Team</span>
                      <strong>{draftState.currentTeam?.name ?? "-"}</strong>
                    </div>
                    <div>
                      <span>Captain</span>
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
                        revealedRole={bag.revealedSlot ? roleLabels[bag.revealedSlot.role] : undefined}
                        revealedScore={bag.revealedSlot?.averageScore}
                        onOpen={() => prepareReveal(bag.id)}
                      />
                    ))}
                  </div>

                  {draftState.lastOpenedBag && (
                    <div className="reveal-card">
                      <Badge tone="orange">Đã khui</Badge>
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
                </div>
              </div>
            )}
          </>
        )}

        {statusMessage && <div className="notice notice-good">{statusMessage}</div>}
        {error && <div className="notice notice-danger">{error}</div>}
      </div>

      <ShootingStarRevealModal
        isOpen={Boolean(pendingReveal)}
        rarity={pendingReveal?.rarity ?? "blue"}
        revealedSlot={pendingReveal?.revealedSlot ?? null}
        captainName={pendingReveal?.captainName ?? draftState?.currentCaptain?.name ?? "Captain"}
        teamName={pendingReveal?.teamName ?? draftState?.currentTeam?.name ?? "Team"}
        onContinue={continueReveal}
      />
    </section>
  );
}
