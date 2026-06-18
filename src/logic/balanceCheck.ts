import type { Player } from "../types/player";
import type { Team } from "../types/team";
import { getSlotScoreForSet } from "./rotation";

export type BalanceStatus = {
  label: string;
  tone: "good" | "warn" | "danger";
  difference: number;
};

export type AverageBalance = {
  teamTotals: Array<{ teamName: string; total: number }>;
  status: BalanceStatus;
};

export type SetBalanceRow = {
  setNumber: number;
  teamTotals: Array<{ teamName: string; total: number }>;
  difference: number;
  status: BalanceStatus;
};

export function getBalanceStatus(difference: number): BalanceStatus {
  if (difference <= 1) {
    return { label: "Cân bằng tốt", tone: "good", difference };
  }

  if (difference <= 2) {
    return { label: "Hơi lệch", tone: "warn", difference };
  }

  return { label: "Lệch mạnh", tone: "danger", difference };
}

function spread(values: number[]) {
  if (values.length === 0) {
    return 0;
  }

  return Math.max(...values) - Math.min(...values);
}

export function calculateAverageBalance(teams: Team[]): AverageBalance {
  const teamTotals = teams.map((team) => ({
    teamName: team.name,
    total: team.slots.reduce((total, slot) => total + slot.averageScore, 0),
  }));

  return {
    teamTotals,
    status: getBalanceStatus(spread(teamTotals.map((team) => team.total))),
  };
}

export function calculateSetBalance(teams: Team[], players: Player[], setCount: number) {
  const rows: SetBalanceRow[] = [];

  for (let setNumber = 1; setNumber <= setCount; setNumber += 1) {
    const teamTotals = teams.map((team) => ({
      teamName: team.name,
      total: team.slots.reduce(
        (total, slot) => total + getSlotScoreForSet(slot, players, setNumber),
        0,
      ),
    }));

    const difference = spread(teamTotals.map((team) => team.total));
    rows.push({
      setNumber,
      teamTotals,
      difference,
      status: getBalanceStatus(difference),
    });
  }

  return rows;
}
