import { Pencil, Plus, Trash2, UsersRound } from "lucide-react";
import { FormEvent, useMemo, useState } from "react";
import { levelLabels, roleLabels } from "../data/mockData";
import { calculatePlayerScore, formatScore } from "../logic/scoring";
import type { Gender, Level, Player, Role } from "../types/player";
import type { SharedSlot } from "../types/slot";
import { LevelBadge, RoleBadge, ScorePill } from "./ui";

const roles = Object.keys(roleLabels) as Role[];
const levels = Object.keys(levelLabels) as Level[];

type PlayerDraft = {
  name: string;
  role: Role;
  level: Level;
  gender: Gender;
};

const emptyDraft: PlayerDraft = {
  name: "",
  role: "Attack",
  level: "Average",
  gender: "Male",
};

export function PlayerList({
  players,
  sharedSlots,
  onPlayersChange,
}: {
  players: Player[];
  sharedSlots: SharedSlot[];
  onPlayersChange: (players: Player[]) => void;
}) {
  const [draft, setDraft] = useState<PlayerDraft>(emptyDraft);
  const [editingId, setEditingId] = useState<string | null>(null);
  const [editDraft, setEditDraft] = useState<PlayerDraft>(emptyDraft);

  const sharedPlayerIds = useMemo(
    () => new Set(sharedSlots.flatMap((slot) => slot.playerIds)),
    [sharedSlots],
  );
  const slotCount = players.length - sharedPlayerIds.size + sharedSlots.length;
  const teamCount = 3;
  const minimumSlots = teamCount * 2;
  const canDivide = slotCount >= minimumSlots && slotCount % teamCount === 0;
  const scorePreview = calculatePlayerScore(draft.role, draft.level);

  function handleSubmit(event: FormEvent<HTMLFormElement>) {
    event.preventDefault();

    const trimmedName = draft.name.trim();
    if (!trimmedName) {
      return;
    }

    const newPlayer: Player = {
      id: `p-${Date.now()}`,
      name: trimmedName,
      role: draft.role,
      level: draft.level,
      gender: draft.gender,
      score: calculatePlayerScore(draft.role, draft.level),
    };

    onPlayersChange([...players, newPlayer]);
    setDraft(emptyDraft);
  }

  function startEdit(player: Player) {
    setEditingId(player.id);
    setEditDraft({
      name: player.name,
      role: player.role,
      level: player.level,
      gender: player.gender,
    });
  }

  function saveEdit(playerId: string) {
    const trimmedName = editDraft.name.trim();
    if (!trimmedName) {
      return;
    }

    onPlayersChange(
      players.map((player) =>
        player.id === playerId
          ? {
              ...player,
              name: trimmedName,
              role: editDraft.role,
              level: editDraft.level,
              gender: editDraft.gender,
              score: calculatePlayerScore(editDraft.role, editDraft.level),
            }
          : player,
      ),
    );
    setEditingId(null);
  }

  return (
    <section className="screen-stack">
      <div className="screen-heading">
        <div>
          <p className="eyebrow">Màn 1</p>
          <h1>Danh sách người chơi</h1>
        </div>
        <div className="heading-icon">
          <UsersRound size={24} aria-hidden="true" />
        </div>
      </div>

      <div className="stat-grid">
        <div className="stat-card">
          <span>Tổng người chơi</span>
          <strong>{players.length}</strong>
        </div>
        <div className="stat-card">
          <span>Tổng slot thi đấu</span>
          <strong>{slotCount}</strong>
        </div>
        <div className="stat-card">
          <span>Số team</span>
          <strong>3</strong>
        </div>
        <div className="stat-card">
          <span>Slot mỗi team</span>
          <strong>{canDivide ? slotCount / teamCount : "—"}</strong>
        </div>
      </div>

      <div className={canDivide ? "notice notice-good" : "notice notice-warn"}>
        {canDivide
          ? `${slotCount} slot có thể chia 3 team (${slotCount / teamCount} slot/team).`
          : `${slotCount} slot chưa thể chia 3 team. Cần từ ${minimumSlots} slot và chia hết cho 3.`}
      </div>

      <form className="tool-panel" onSubmit={handleSubmit}>
        <div className="form-grid">
          <label className="field">
            <span>Tên người chơi</span>
            <input
              className="input"
              value={draft.name}
              onChange={(event) => setDraft({ ...draft, name: event.target.value })}
              placeholder="Ví dụ: Mai"
            />
          </label>

          <label className="field">
            <span>Vai trò</span>
            <select
              className="input"
              value={draft.role}
              onChange={(event) => setDraft({ ...draft, role: event.target.value as Role })}
            >
              {roles.map((role) => (
                <option key={role} value={role}>
                  {roleLabels[role]}
                </option>
              ))}
            </select>
          </label>

          <label className="field">
            <span>Trình độ</span>
            <select
              className="input"
              value={draft.level}
              onChange={(event) => setDraft({ ...draft, level: event.target.value as Level })}
            >
              {levels.map((level) => (
                <option key={level} value={level}>
                  {levelLabels[level]}
                </option>
              ))}
            </select>
          </label>

          <div className="score-preview">
            <span>Điểm tự động</span>
            <strong>{formatScore(scorePreview)}</strong>
          </div>
        </div>

        <button className="button-primary" type="submit">
          <Plus size={18} aria-hidden="true" />
          Thêm người chơi
        </button>
      </form>

      <div className="table-shell">
        <div className="desktop-table">
          <table>
            <thead>
              <tr>
                <th>Tên</th>
                <th>Vai trò</th>
                <th>Trình độ</th>
                <th>Điểm</th>
                <th>Thao tác</th>
              </tr>
            </thead>
            <tbody>
              {players.map((player) => {
                const isEditing = editingId === player.id;
                const isInSharedSlot = sharedPlayerIds.has(player.id);

                return (
                  <tr key={player.id}>
                    <td>
                      {isEditing ? (
                        <input
                          className="input input-compact"
                          value={editDraft.name}
                          onChange={(event) =>
                            setEditDraft({ ...editDraft, name: event.target.value })
                          }
                        />
                      ) : (
                        <span className="font-semibold">{player.name}</span>
                      )}
                    </td>
                    <td>
                      {isEditing ? (
                        <select
                          className="input input-compact"
                          value={editDraft.role}
                          onChange={(event) =>
                            setEditDraft({ ...editDraft, role: event.target.value as Role })
                          }
                        >
                          {roles.map((role) => (
                            <option key={role} value={role}>
                              {roleLabels[role]}
                            </option>
                          ))}
                        </select>
                      ) : (
                        <RoleBadge role={player.role} />
                      )}
                    </td>
                    <td>
                      {isEditing ? (
                        <select
                          className="input input-compact"
                          value={editDraft.level}
                          onChange={(event) =>
                            setEditDraft({ ...editDraft, level: event.target.value as Level })
                          }
                        >
                          {levels.map((level) => (
                            <option key={level} value={level}>
                              {levelLabels[level]}
                            </option>
                          ))}
                        </select>
                      ) : (
                        <LevelBadge level={player.level} />
                      )}
                    </td>
                    <td>
                      <ScorePill
                        score={
                          isEditing
                            ? calculatePlayerScore(editDraft.role, editDraft.level)
                            : player.score
                        }
                      />
                    </td>
                    <td>
                      <div className="action-row">
                        {isEditing ? (
                          <>
                            <button
                              type="button"
                              className="button-secondary button-small"
                              onClick={() => saveEdit(player.id)}
                            >
                              Lưu
                            </button>
                            <button
                              type="button"
                              className="button-ghost button-small"
                              onClick={() => setEditingId(null)}
                            >
                              Hủy
                            </button>
                          </>
                        ) : (
                          <>
                            <button
                              type="button"
                              className="icon-button"
                              onClick={() => startEdit(player)}
                              aria-label={`Sửa ${player.name}`}
                              title="Sửa"
                            >
                              <Pencil size={16} aria-hidden="true" />
                            </button>
                            <button
                              type="button"
                              className="icon-button danger"
                              onClick={() =>
                                onPlayersChange(players.filter((item) => item.id !== player.id))
                              }
                              disabled={isInSharedSlot}
                              aria-label={`Xóa ${player.name}`}
                              title={isInSharedSlot ? "Đang ở slot thay phiên" : "Xóa"}
                            >
                              <Trash2 size={16} aria-hidden="true" />
                            </button>
                          </>
                        )}
                      </div>
                    </td>
                  </tr>
                );
              })}
            </tbody>
          </table>
        </div>

        <div className="mobile-list">
          {players.map((player) => (
            <article className="mini-card" key={player.id}>
              <div>
                <h3>{player.name}</h3>
                <div className="badge-row">
                  <RoleBadge role={player.role} />
                  <LevelBadge level={player.level} />
                </div>
              </div>
              <ScorePill score={player.score} />
            </article>
          ))}
        </div>
      </div>
    </section>
  );
}
