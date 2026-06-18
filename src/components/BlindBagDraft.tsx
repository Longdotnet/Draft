import { useState } from "react";
import { Gift, Sparkles, Trophy } from "lucide-react";
import type { DraftSlot } from "../types/slot";
import type { DraftRound, OpenedBag, Team } from "../types/team";
import { BlindBagCard } from "./draft/BlindBagCard";
import { ShootingStarRevealModal } from "./draft/ShootingStarRevealModal";
import { Badge, ScorePill } from "./ui";
import { getRevealRarity, type RevealRarity } from "../lib/revealRarity";

type PendingReveal = {
  unopenedIndex: number;
  slot: DraftSlot;
  rarity: RevealRarity;
  captainName: string;
  teamName: string;
};

export function BlindBagDraft({
  teams,
  draftRounds,
  currentRoundIndex,
  currentTeamIndex,
  openedBags,
  onOpenBag,
  onAutoComplete,
  onResetDraft,
}: {
  teams: Team[];
  draftRounds: DraftRound[];
  currentRoundIndex: number;
  currentTeamIndex: number;
  openedBags: OpenedBag[];
  onOpenBag: (bagIndex: number) => void;
  onAutoComplete: () => void;
  onResetDraft: () => void;
}) {
  const [pendingReveal, setPendingReveal] = useState<PendingReveal | null>(null);
  const isComplete = currentRoundIndex >= draftRounds.length;
  const currentRound = draftRounds[currentRoundIndex];
  const currentTeam = teams[currentTeamIndex] ?? teams[0];
  const captainSlot = currentTeam?.slots.find((slot) => slot.id === currentTeam.captainSlotId);
  const captainName = captainSlot?.displayName ?? "Đại diện";
  const openedSlotIds = new Set(
    openedBags.filter((bag) => bag.roundIndex === currentRoundIndex).map((bag) => bag.slot.id),
  );
  const unopenedSlots = currentRound?.slots.filter((slot) => !openedSlotIds.has(slot.id)) ?? [];
  const lastReveal = openedBags[openedBags.length - 1];
  const nextTeam = isComplete ? null : teams[(currentTeamIndex + 1) % teams.length];

  function prepareReveal(slot: DraftSlot, unopenedIndex: number) {
    if (isComplete || unopenedIndex < 0 || pendingReveal) {
      return;
    }

    setPendingReveal({
      unopenedIndex,
      slot,
      rarity: getRevealRarity(slot.averageScore),
      captainName,
      teamName: currentTeam.name,
    });
  }

  function continueReveal() {
    if (!pendingReveal) {
      return;
    }

    onOpenBag(pendingReveal.unopenedIndex);
    setPendingReveal(null);
  }

  return (
    <section className="screen-stack draft-screen">
      <div className="screen-heading inverted">
        <div>
          <p className="eyebrow">Màn 4</p>
          <h1>Đội hình bốc theo túi mù</h1>
        </div>
        <div className="heading-icon">
          <Gift size={24} aria-hidden="true" />
        </div>
      </div>

      <div className="draft-status">
        <div>
          <span>Vòng bốc</span>
          <strong>
            {isComplete ? draftRounds.length : currentRoundIndex + 1} / {draftRounds.length}
          </strong>
        </div>
        <div>
          <span>Nhóm hiện tại</span>
          <strong>{isComplete ? "Hoàn tất" : currentRound.label}</strong>
        </div>
        <div>
          <span>Lượt hiện tại</span>
          <strong>{isComplete ? "Đã xong" : currentTeam.name}</strong>
        </div>
        <div>
          <span>Đại diện</span>
          <strong>{isComplete ? "Tất cả" : captainName}</strong>
        </div>
      </div>

      <div className="draft-stage">
        {isComplete ? (
          <div className="reveal-card complete">
            <Trophy size={28} aria-hidden="true" />
            <h2>Draft đã hoàn tất</h2>
            <p>3 team đã có đủ slot để xem kết quả, lịch thay phiên và cân bằng điểm.</p>
          </div>
        ) : (
          <>
            <div className="draft-instruction">
              <Sparkles size={20} aria-hidden="true" />
              <span>
                {captainName}, hãy khui một túi mù cho {currentTeam.name}.
              </span>
            </div>

            <div className="bag-grid">
              {currentRound.slots.map((slot, index) => {
                const isOpened = openedSlotIds.has(slot.id);
                const unopenedIndex = unopenedSlots.findIndex((item) => item.id === slot.id);

                return (
                  <BlindBagCard
                    key={slot.id}
                    bagNumber={index + 1}
                    isOpened={isOpened}
                    isDisabled={isOpened || Boolean(pendingReveal)}
                    revealedName={isOpened ? slot.displayName : undefined}
                    revealedRole={isOpened ? slot.role : undefined}
                    revealedScore={isOpened ? slot.averageScore : undefined}
                    onOpen={() => prepareReveal(slot, unopenedIndex)}
                  />
                );
              })}
            </div>
          </>
        )}

        {lastReveal && (
          <div className="reveal-card">
            <Badge tone={lastReveal.slot.type === "shared" ? "violet" : "orange"}>
              Đã khui được
            </Badge>
            <p>
              {lastReveal.captainName} đã khui túi và bốc được{" "}
              <strong>{lastReveal.slot.displayName}</strong> cho {lastReveal.teamName}.
            </p>
            <ScorePill score={lastReveal.slot.averageScore} />
          </div>
        )}

        {!isComplete && nextTeam && (
          <p className="next-turn">
            Tiếp theo: {currentTeamIndex === teams.length - 1 ? teams[0].name : nextTeam.name}
          </p>
        )}

        <div className="action-row">
          <button className="button-secondary" type="button" onClick={onAutoComplete}>
            Hoàn tất demo draft
          </button>
          <button className="button-ghost dark" type="button" onClick={onResetDraft}>
            Làm mới draft
          </button>
        </div>
      </div>

      <div className="team-preview-grid">
        {teams.map((team) => {
          const captain = team.slots.find((slot) => slot.id === team.captainSlotId);

          return (
            <article className={`team-preview team-${team.accent}`} key={team.id}>
              <div className="card-title-row">
                <div>
                  <h3>{team.name}</h3>
                  <p>Captain: {captain?.displayName ?? "-"}</p>
                </div>
                <strong>{team.slots.length}/6</strong>
              </div>
              <div className="member-line">
                {team.slots.map((slot) => slot.displayName).join(", ")}
              </div>
            </article>
          );
        })}
      </div>

      <ShootingStarRevealModal
        isOpen={Boolean(pendingReveal)}
        rarity={pendingReveal?.rarity ?? "blue"}
        revealedSlot={pendingReveal?.slot ?? null}
        captainName={pendingReveal?.captainName ?? captainName}
        teamName={pendingReveal?.teamName ?? currentTeam.name}
        onContinue={continueReveal}
      />
    </section>
  );
}
