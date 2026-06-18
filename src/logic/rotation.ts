import type { Player } from "../types/player";
import type { DraftSlot } from "../types/slot";
import type { RotationEntry, Team } from "../types/team";

export function getPlayerForSet(slot: DraftSlot, players: Player[], setNumber: number) {
  const playerId = slot.playerIds[(setNumber - 1) % slot.playerIds.length];
  return players.find((player) => player.id === playerId);
}

export function getSlotScoreForSet(slot: DraftSlot, players: Player[], setNumber: number) {
  if (slot.type === "single") {
    return slot.averageScore;
  }

  return getPlayerForSet(slot, players, setNumber)?.score ?? slot.averageScore;
}

export function buildRotationEntries(teams: Team[], players: Player[], setCount: number) {
  const entries: RotationEntry[] = [];

  teams.forEach((team) => {
    team.slots
      .filter((slot) => slot.type === "shared")
      .forEach((slot) => {
        for (let setNumber = 1; setNumber <= setCount; setNumber += 1) {
          const player = getPlayerForSet(slot, players, setNumber);

          if (player) {
            entries.push({
              setNumber,
              teamId: team.id,
              teamName: team.name,
              slotId: slot.id,
              slotName: slot.displayName,
              playerId: player.id,
              playerName: player.name,
              score: player.score,
            });
          }
        }
      });
  });

  return entries.sort((left, right) => left.setNumber - right.setNumber);
}
