import type { Player } from "../types/player";
import type { DraftSlot, SharedSlot } from "../types/slot";
import type { DraftRound, Team, TeamId } from "../types/team";
import { averageScore } from "./scoring";

export const TEAM_TEMPLATES: Array<Omit<Team, "slots" | "captainSlotId">> = [
  { id: "team-a", name: "Team A", accent: "orange" },
  { id: "team-b", name: "Team B", accent: "sky" },
  { id: "team-c", name: "Team C", accent: "violet" },
];

const ROUND_LABELS = [
  "Cầu thủ mạnh",
  "Nhóm ổn định",
  "Nhóm cân bằng",
  "Nhóm tiềm năng",
  "Nhóm bất ngờ",
];

export function getSharedPlayerIds(sharedSlots: SharedSlot[]) {
  return new Set(sharedSlots.flatMap((slot) => slot.playerIds));
}

export function createSingleSlot(player: Player): DraftSlot {
  return {
    id: `slot-${player.id}`,
    type: "single",
    displayName: player.name,
    playerIds: [player.id],
    role: player.role,
    gender: player.gender,
    averageScore: player.score,
    isCaptainEligible: true,
  };
}

export function createSharedDraftSlot(sharedSlot: SharedSlot, players: Player[]): DraftSlot | null {
  const members = sharedSlot.playerIds
    .map((playerId) => players.find((player) => player.id === playerId))
    .filter((player): player is Player => Boolean(player));

  if (members.length < 2) {
    return null;
  }

  return {
    id: sharedSlot.id,
    type: "shared",
    displayName: members.map((player) => player.name).join(" / "),
    playerIds: members.map((player) => player.id),
    role: sharedSlot.role,
    gender: members.some((player) => player.gender === "Female") ? "Female" : "Male",
    averageScore: averageScore(members),
    isCaptainEligible: false,
  };
}

export function buildDraftSlots(players: Player[], sharedSlots: SharedSlot[]) {
  const sharedPlayerIds = getSharedPlayerIds(sharedSlots);
  const singleSlots = players
    .filter((player) => !sharedPlayerIds.has(player.id))
    .map(createSingleSlot);

  const sharedDraftSlots = sharedSlots
    .map((sharedSlot) => createSharedDraftSlot(sharedSlot, players))
    .filter((slot): slot is DraftSlot => Boolean(slot));

  return [...singleSlots, ...sharedDraftSlots];
}

export function findCaptainSlot(playerId: string, draftSlots: DraftSlot[]) {
  return draftSlots.find(
    (slot) => slot.type === "single" && slot.playerIds[0] === playerId && slot.isCaptainEligible,
  );
}

function shuffle<T>(items: T[]) {
  return [...items].sort(() => Math.random() - 0.5);
}

function takeSlots(slots: DraftSlot[], count: number) {
  return slots.splice(0, count);
}

function buildGenderBalancedRandomPool(slots: DraftSlot[]) {
  const femaleSlots = shuffle(slots.filter((slot) => slot.gender === "Female"));
  const otherSlots = shuffle(slots.filter((slot) => slot.gender !== "Female"));
  const pool: DraftSlot[] = [];

  while (femaleSlots.length >= TEAM_TEMPLATES.length) {
    pool.push(...takeSlots(femaleSlots, TEAM_TEMPLATES.length));
  }

  if (femaleSlots.length > 0) {
    const mixedRound = takeSlots(femaleSlots, femaleSlots.length);
    mixedRound.push(...takeSlots(otherSlots, TEAM_TEMPLATES.length - mixedRound.length));
    pool.push(...shuffle(mixedRound));
  }

  while (otherSlots.length > 0) {
    pool.push(...shuffle(takeSlots(otherSlots, Math.min(TEAM_TEMPLATES.length, otherSlots.length))));
  }

  return pool;
}

function shuffleWithinRound(slots: DraftSlot[]) {
  return [...slots].sort(() => Math.random() - 0.5);
}

export function createDraftRounds(draftSlots: DraftSlot[], captainSlotIds: string[]) {
  const captainIdSet = new Set(captainSlotIds);
  const remainingSlots = buildGenderBalancedRandomPool(
    draftSlots.filter((slot) => !captainIdSet.has(slot.id)),
  );

  const rounds: DraftRound[] = [];
  for (let index = 0; index < remainingSlots.length; index += TEAM_TEMPLATES.length) {
    const slots = remainingSlots.slice(index, index + TEAM_TEMPLATES.length);
    rounds.push({
      id: `round-${rounds.length + 1}`,
      roundNumber: rounds.length + 1,
      label: ROUND_LABELS[rounds.length] ?? "Vòng bổ sung",
      slots: shuffleWithinRound(slots),
    });
  }

  return rounds;
}

export function createInitialTeams(captainSlots: DraftSlot[]) {
  return TEAM_TEMPLATES.map((template, index) => {
    const captainSlot = captainSlots[index];

    return {
      ...template,
      captainSlotId: captainSlot?.id,
      slots: captainSlot ? [captainSlot] : [],
    };
  });
}

export function getTeamById(teams: Team[], teamId: TeamId) {
  return teams.find((team) => team.id === teamId);
}
