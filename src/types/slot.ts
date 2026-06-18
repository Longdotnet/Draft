import type { Role } from "./player";

export type SlotType = "single" | "shared";

export type DraftSlot = {
  id: string;
  type: SlotType;
  displayName: string;
  playerIds: string[];
  role: Role;
  gender: "Male" | "Female";
  averageScore: number;
  isCaptainEligible: boolean;
};

export type SharedSlot = {
  id: string;
  playerIds: string[];
  role: Role;
  setCount: number;
};
