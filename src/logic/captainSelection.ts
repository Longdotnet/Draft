import type { Player } from "../types/player";
import type { SharedSlot } from "../types/slot";
import { getSharedPlayerIds } from "./draftRounds";

export type CaptainBalance = {
  difference: number;
  label: string;
  tone: "good" | "warn" | "danger";
  message: string;
};

export function getCaptainEligiblePlayers(players: Player[], sharedSlots: SharedSlot[]) {
  const sharedPlayerIds = getSharedPlayerIds(sharedSlots);
  return players.filter((player) => !sharedPlayerIds.has(player.id));
}

function combinations<T>(items: T[], size: number): T[][] {
  if (size === 0) {
    return [[]];
  }

  return items.flatMap((item, index) =>
    combinations(items.slice(index + 1), size - 1).map((combo) => [item, ...combo]),
  );
}

function scoreSpread(players: Player[]) {
  const scores = players.map((player) => player.score);
  return Math.max(...scores) - Math.min(...scores);
}

export function selectBalancedCaptains(players: Player[], sharedSlots: SharedSlot[]) {
  const eligiblePlayers = getCaptainEligiblePlayers(players, sharedSlots);
  const combos = combinations(eligiblePlayers, 3);

  if (combos.length === 0) {
    return [];
  }

  const bestSpread = Math.min(...combos.map(scoreSpread));
  const acceptableSpread = bestSpread <= 0.5 ? 0.5 : bestSpread <= 1 ? 1 : bestSpread;
  const targetFemaleCaptains = Math.min(
    3,
    eligiblePlayers.filter((player) => player.gender === "Female").length,
  );
  const candidates = combos
    .filter((combo) => scoreSpread(combo) <= acceptableSpread)
    .sort(
      (left, right) =>
        Math.abs(left.filter((player) => player.gender === "Female").length - targetFemaleCaptains) -
        Math.abs(right.filter((player) => player.gender === "Female").length - targetFemaleCaptains),
    )
    .slice(0, 12);

  return candidates[Math.floor(Math.random() * candidates.length)];
}

export function evaluateCaptainBalance(players: Player[]): CaptainBalance {
  if (players.length < 3) {
    return {
      difference: 0,
      label: "Chưa đủ đại diện",
      tone: "warn",
      message: "Cần chọn đủ 3 đại diện trước khi bắt đầu bốc túi.",
    };
  }

  const difference = scoreSpread(players);

  if (difference <= 0.5) {
    return {
      difference,
      label: "Cân bằng tốt",
      tone: "good",
      message: "3 đại diện có trình độ rất gần nhau.",
    };
  }

  if (difference <= 1) {
    return {
      difference,
      label: "Hơi lệch",
      tone: "warn",
      message: "Có lệch nhẹ giữa các đại diện, vẫn có thể chơi vui.",
    };
  }

  return {
    difference,
    label: "Lệch mạnh",
    tone: "danger",
    message: "3 đại diện đang lệch trình. Một team có thể có lợi thế ngay từ đầu.",
  };
}
