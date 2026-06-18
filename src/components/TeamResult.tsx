import { Crown, UsersRound } from "lucide-react";
import { calculateAverageBalance } from "../logic/balanceCheck";
import { formatScore } from "../logic/scoring";
import type { Team } from "../types/team";
import { Badge, RoleBadge, ScorePill, StatusBadge } from "./ui";

export function TeamResult({ teams }: { teams: Team[] }) {
  const balance = calculateAverageBalance(teams);

  return (
    <section className="screen-stack">
      <div className="screen-heading">
        <div>
          <p className="eyebrow">Màn 5</p>
          <h1>Kết quả đội hình</h1>
        </div>
        <div className="heading-icon">
          <UsersRound size={24} aria-hidden="true" />
        </div>
      </div>

      <div className={`notice notice-${balance.status.tone}`}>
        <StatusBadge tone={balance.status.tone} label={balance.status.label} />
        <span>Chênh lệch tổng điểm trung bình: {formatScore(balance.status.difference)}.</span>
      </div>

      <div className="team-card-grid">
        {teams.map((team) => {
          const total = team.slots.reduce((sum, slot) => sum + slot.averageScore, 0);
          const average = team.slots.length ? total / team.slots.length : 0;

          return (
            <article className={`team-card team-${team.accent}`} key={team.id}>
              <div className="card-title-row">
                <div>
                  <h2>{team.name}</h2>
                  <p>
                    Tổng điểm: <strong>{formatScore(total)}</strong>
                  </p>
                </div>
                <ScorePill score={average} label="avg/slot" />
              </div>

              <div className="member-list">
                {team.slots.map((slot) => {
                  const isCaptain = slot.id === team.captainSlotId;

                  return (
                    <div className="member-row" key={slot.id}>
                      <div>
                        <strong>{slot.displayName}</strong>
                        <div className="badge-row">
                          {isCaptain && (
                            <Badge tone="orange">
                              <Crown size={13} aria-hidden="true" />
                              Captain
                            </Badge>
                          )}
                          {slot.type === "shared" && <Badge tone="violet">Slot thay phiên</Badge>}
                          <RoleBadge role={slot.role} />
                        </div>
                      </div>
                      <ScorePill
                        score={slot.averageScore}
                        label={slot.type === "shared" ? "avg" : "điểm"}
                      />
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
