import { Link2, Plus, RotateCcw } from "lucide-react";
import { FormEvent, useMemo, useState } from "react";
import { roleLabels } from "../data/mockData";
import { createSharedDraftSlot, getSharedPlayerIds } from "../logic/draftRounds";
import { getPlayerForSet } from "../logic/rotation";
import { formatScore } from "../logic/scoring";
import type { Player, Role } from "../types/player";
import type { SharedSlot } from "../types/slot";
import { Badge, RoleBadge, ScorePill } from "./ui";

const roles = Object.keys(roleLabels) as Role[];

export function SharedSlotSetup({
  players,
  sharedSlots,
  onSharedSlotsChange,
}: {
  players: Player[];
  sharedSlots: SharedSlot[];
  onSharedSlotsChange: (sharedSlots: SharedSlot[]) => void;
}) {
  const [selectedPlayerIds, setSelectedPlayerIds] = useState<string[]>([]);
  const [role, setRole] = useState<Role>("Attack");
  const [setCount, setSetCount] = useState(4);

  const sharedPlayerIds = useMemo(
    () => getSharedPlayerIds(sharedSlots),
    [sharedSlots],
  );
  const slotCount = players.length - sharedPlayerIds.size + sharedSlots.length;
  const availablePlayers = players.filter((player) => !sharedPlayerIds.has(player.id));

  function togglePlayer(playerId: string) {
    setSelectedPlayerIds((current) =>
      current.includes(playerId)
        ? current.filter((id) => id !== playerId)
        : [...current, playerId],
    );
  }

  function handleCreateSlot(event: FormEvent<HTMLFormElement>) {
    event.preventDefault();

    if (selectedPlayerIds.length < 2) {
      return;
    }

    onSharedSlotsChange([
      ...sharedSlots,
      {
        id: `shared-${Date.now()}`,
        playerIds: selectedPlayerIds,
        role,
        setCount,
      },
    ]);
    setSelectedPlayerIds([]);
    setRole("Attack");
    setSetCount(4);
  }

  return (
    <section className="screen-stack">
      <div className="screen-heading">
        <div>
          <p className="eyebrow">Màn 2</p>
          <h1>Slot thay phiên</h1>
          <p className="screen-copy">
            Ghép 2 hoặc nhiều người vào cùng 1 slot thi đấu. Họ sẽ thay phiên nhau theo từng set.
          </p>
        </div>
        <div className="heading-icon">
          <Link2 size={24} aria-hidden="true" />
        </div>
      </div>

      <div className="stat-grid compact">
        <div className="stat-card">
          <span>Người chơi thật</span>
          <strong>{players.length}</strong>
        </div>
        <div className="stat-card">
          <span>Slot thi đấu</span>
          <strong>{slotCount}</strong>
        </div>
      </div>

      <form className="tool-panel" onSubmit={handleCreateSlot}>
        <div className="field">
          <span>Chọn người share slot</span>
          <div className="chip-grid">
            {availablePlayers.map((player) => (
              <button
                className={
                  selectedPlayerIds.includes(player.id) ? "select-chip active" : "select-chip"
                }
                key={player.id}
                type="button"
                onClick={() => togglePlayer(player.id)}
              >
                {player.name}
                <small>{formatScore(player.score)}</small>
              </button>
            ))}
          </div>
        </div>

        <div className="form-grid">
          <label className="field">
            <span>Vai trò slot</span>
            <select
              className="input"
              value={role}
              onChange={(event) => setRole(event.target.value as Role)}
            >
              {roles.map((item) => (
                <option key={item} value={item}>
                  {roleLabels[item]}
                </option>
              ))}
            </select>
          </label>

          <label className="field">
            <span>Cách tính điểm</span>
            <select className="input" value="average" disabled>
              <option value="average">Trung bình / theo từng set</option>
            </select>
          </label>

          <label className="field">
            <span>Số set dự kiến</span>
            <input
              className="input"
              min={2}
              max={6}
              type="number"
              value={setCount}
              onChange={(event) => setSetCount(Number(event.target.value))}
            />
          </label>
        </div>

        <button className="button-primary" type="submit" disabled={selectedPlayerIds.length < 2}>
          <Plus size={18} aria-hidden="true" />
          Tạo slot thay phiên
        </button>
      </form>

      <div className="card-grid">
        {sharedSlots.map((sharedSlot) => {
          const draftSlot = createSharedDraftSlot(sharedSlot, players);
          if (!draftSlot) {
            return null;
          }

          return (
            <article className="feature-card" key={sharedSlot.id}>
              <div className="card-title-row">
                <div>
                  <h3>{draftSlot.displayName}</h3>
                  <div className="badge-row">
                    <Badge tone="violet">Slot thay phiên</Badge>
                    <RoleBadge role={draftSlot.role} />
                  </div>
                </div>
                <ScorePill score={draftSlot.averageScore} label="avg" />
              </div>

              <div className="rotation-preview">
                {Array.from({ length: sharedSlot.setCount }).map((_, index) => {
                  const setNumber = index + 1;
                  const player = getPlayerForSet(draftSlot, players, setNumber);

                  return (
                    <div key={setNumber}>
                      <RotateCcw size={15} aria-hidden="true" />
                      <span>Set {setNumber}</span>
                      <strong>{player?.name}</strong>
                      <small>{player ? formatScore(player.score) : "-"} điểm</small>
                    </div>
                  );
                })}
              </div>
            </article>
          );
        })}
      </div>
    </section>
  );
}
