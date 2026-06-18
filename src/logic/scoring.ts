import type { Level, Player, Role } from "../types/player";

export const LEVEL_SCORE: Record<Level, number> = {
  Good: 3,
  Average: 2,
  New: 1,
};

export function calculatePlayerScore(role: Role, level: Level) {
  return LEVEL_SCORE[level] + (role === "Full stack" ? 0.5 : 0);
}

export function averageScore(players: Player[]) {
  if (players.length === 0) {
    return 0;
  }

  return players.reduce((total, player) => total + player.score, 0) / players.length;
}

export function formatScore(score: number) {
  return Number.isInteger(score) ? String(score) : score.toFixed(1);
}
