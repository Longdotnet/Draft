import { BarChart3, Lightbulb } from "lucide-react";
import { calculateAverageBalance, calculateSetBalance } from "../logic/balanceCheck";
import { formatScore } from "../logic/scoring";
import type { Player } from "../types/player";
import type { Team } from "../types/team";
import { StatusBadge } from "./ui";

export function BalanceCheck({
  teams,
  players,
  setCount,
}: {
  teams: Team[];
  players: Player[];
  setCount: number;
}) {
  const averageBalance = calculateAverageBalance(teams);
  const setRows = calculateSetBalance(teams, players, setCount);
  const strongestSet = setRows.reduce(
    (current, row) => (row.difference > current.difference ? row : current),
    setRows[0],
  );

  return (
    <section className="screen-stack">
      <div className="screen-heading">
        <div>
          <p className="eyebrow">Màn 7</p>
          <h1>Kiểm tra cân bằng</h1>
        </div>
        <div className="heading-icon">
          <BarChart3 size={24} aria-hidden="true" />
        </div>
      </div>

      <div className="analysis-grid">
        <article className="feature-card">
          <div className="card-title-row">
            <div>
              <h2>Cân bằng điểm trung bình</h2>
              <p className="muted">Tính theo average score của mỗi slot.</p>
            </div>
            <StatusBadge
              tone={averageBalance.status.tone}
              label={averageBalance.status.label}
            />
          </div>
          <div className="balance-bars">
            {averageBalance.teamTotals.map((team) => (
              <div key={team.teamName}>
                <span>{team.teamName}</span>
                <strong>{formatScore(team.total)}</strong>
                <div>
                  <i style={{ width: `${Math.min(100, (team.total / 14) * 100)}%` }} />
                </div>
              </div>
            ))}
          </div>
        </article>

        {strongestSet && strongestSet.difference > 1 && (
          <article className="feature-card warning-card">
            <Lightbulb size={22} aria-hidden="true" />
            <h2>Gợi ý</h2>
            <p>
              Set {strongestSet.setNumber} lệch {formatScore(strongestSet.difference)} điểm. Có
              thể đổi lịch xoay tua hoặc đưa slot share sang team khác.
            </p>
          </article>
        )}
      </div>

      <div className="table-shell">
        <table>
          <thead>
            <tr>
              <th>Set</th>
              {teams.map((team) => (
                <th key={team.id}>{team.name}</th>
              ))}
              <th>Chênh lệch</th>
              <th>Đánh giá</th>
            </tr>
          </thead>
          <tbody>
            {setRows.map((row) => (
              <tr key={row.setNumber}>
                <td>Set {row.setNumber}</td>
                {row.teamTotals.map((team) => (
                  <td key={team.teamName}>{formatScore(team.total)}</td>
                ))}
                <td>{formatScore(row.difference)}</td>
                <td>
                  <StatusBadge tone={row.status.tone} label={row.status.label} />
                </td>
              </tr>
            ))}
          </tbody>
        </table>
      </div>
    </section>
  );
}
