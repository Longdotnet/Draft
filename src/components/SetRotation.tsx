import { RotateCcw } from "lucide-react";
import { buildRotationEntries } from "../logic/rotation";
import { formatScore } from "../logic/scoring";
import type { Player } from "../types/player";
import type { Team } from "../types/team";
import { Badge } from "./ui";

export function SetRotation({
  teams,
  players,
  setCount,
}: {
  teams: Team[];
  players: Player[];
  setCount: number;
}) {
  const entries = buildRotationEntries(teams, players, setCount);

  return (
    <section className="screen-stack">
      <div className="screen-heading">
        <div>
          <p className="eyebrow">Màn 6</p>
          <h1>Lịch thay phiên theo set</h1>
          <p className="screen-copy">
            Slot thay phiên sẽ đổi người chơi theo từng set để giữ đúng số slot thi đấu.
          </p>
        </div>
        <div className="heading-icon">
          <RotateCcw size={24} aria-hidden="true" />
        </div>
      </div>

      {entries.length === 0 ? (
        <div className="empty-state">
          Chưa có slot thay phiên nào nằm trong đội hình hiện tại. Hãy hoàn tất draft demo để xem
          lịch.
        </div>
      ) : (
        <div className="table-shell">
          <table>
            <thead>
              <tr>
                <th>Set</th>
                <th>Team</th>
                <th>Slot thay phiên</th>
                <th>Người vào sân</th>
                <th>Điểm</th>
              </tr>
            </thead>
            <tbody>
              {entries.map((entry) => (
                <tr key={`${entry.teamId}-${entry.slotId}-${entry.setNumber}`}>
                  <td>Set {entry.setNumber}</td>
                  <td>{entry.teamName}</td>
                  <td>
                    <div className="badge-row">
                      <span className="font-semibold">{entry.slotName}</span>
                      <Badge tone="violet">Slot thay phiên</Badge>
                    </div>
                  </td>
                  <td>{entry.playerName}</td>
                  <td>{formatScore(entry.score)}</td>
                </tr>
              ))}
            </tbody>
          </table>
        </div>
      )}
    </section>
  );
}
