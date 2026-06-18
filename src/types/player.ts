export type Role = "Attack" | "Defense" | "Setter" | "Full stack" | "New";

export type Level = "Good" | "Average" | "New";

export type Gender = "Male" | "Female";

export type Player = {
  id: string;
  name: string;
  role: Role;
  level: Level;
  gender: Gender;
  score: number;
};
