import type { DraftSlot } from "./slot";

export type TeamId = "team-a" | "team-b" | "team-c";

export type Team = {
  id: TeamId;
  name: string;
  captainSlotId?: string;
  slots: DraftSlot[];
  accent: "orange" | "sky" | "violet";
};

export type RotationEntry = {
  setNumber: number;
  teamId: TeamId;
  teamName: string;
  slotId: string;
  slotName: string;
  playerId: string;
  playerName: string;
  score: number;
};

export type DraftRound = {
  id: string;
  roundNumber: number;
  label: string;
  slots: DraftSlot[];
};

export type OpenedBag = {
  id: string;
  roundIndex: number;
  roundNumber: number;
  teamId: TeamId;
  teamName: string;
  captainName: string;
  bagLabel: string;
  slot: DraftSlot;
};
