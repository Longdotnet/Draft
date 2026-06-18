import { CheckCircle2, Crown, RefreshCw, ShieldAlert } from "lucide-react";
import { useMemo, useState } from "react";
import { evaluateCaptainBalance, getCaptainEligiblePlayers } from "../logic/captainSelection";
import { formatScore } from "../logic/scoring";
import type { Player } from "../types/player";
import type { SharedSlot } from "../types/slot";
import { ScorePill, StatusBadge } from "./ui";

type Mode = "auto" | "manual";

export function CaptainSelection({
  players,
  sharedSlots,
  captainIds,
  onRandomize,
  onConfirm,
}: {
  players: Player[];
  sharedSlots: SharedSlot[];
  captainIds: string[];
  onRandomize: () => void;
  onConfirm: (captainIds: string[]) => void;
}) {
  const [mode, setMode] = useState<Mode>("auto");
  const [manualCaptainIds, setManualCaptainIds] = useState<string[]>(captainIds);

  const eligiblePlayers = useMemo(
    () => getCaptainEligiblePlayers(players, sharedSlots),
    [players, sharedSlots],
  );
  const activeCaptainIds = mode === "auto" ? captainIds : manualCaptainIds;
  const selectedCaptains = activeCaptainIds
    .map((captainId) => players.find((player) => player.id === captainId))
    .filter((player): player is Player => Boolean(player));
  const balance = evaluateCaptainBalance(selectedCaptains);
  const hasDuplicates = new Set(activeCaptainIds.filter(Boolean)).size !== activeCaptainIds.length;
  const canConfirm = selectedCaptains.length === 3 && !hasDuplicates;

  function updateManualCaptain(index: number, playerId: string) {
    const nextIds = [...manualCaptainIds];
    nextIds[index] = playerId;
    setManualCaptainIds(nextIds);
  }

  function handleModeChange(nextMode: Mode) {
    setMode(nextMode);
    if (nextMode === "manual") {
      setManualCaptainIds(captainIds);
    }
  }

  return (
    <section className="screen-stack">
      <div className="screen-heading">
        <div>
          <p className="eyebrow">Màn 3</p>
          <h1>Chọn đại diện / đội trưởng</h1>
        </div>
        <div className="heading-icon">
          <Crown size={24} aria-hidden="true" />
        </div>
      </div>

      <div className="segmented-control" role="tablist" aria-label="Chế độ chọn đại diện">
        <button
          type="button"
          className={mode === "auto" ? "active" : ""}
          onClick={() => handleModeChange("auto")}
        >
          Auto cân bằng
        </button>
        <button
          type="button"
          className={mode === "manual" ? "active" : ""}
          onClick={() => handleModeChange("manual")}
        >
          Admin chọn tay
        </button>
      </div>

      {mode === "auto" ? (
        <div className="tool-panel">
          <div className="card-title-row">
            <div>
              <h2>Random 3 đại diện cân bằng</h2>
              <p className="muted">Ưu tiên chênh lệch điểm đại diện không quá 0.5.</p>
            </div>
            <button className="button-secondary" type="button" onClick={onRandomize}>
              <RefreshCw size={17} aria-hidden="true" />
              Random lại
            </button>
          </div>

          <div className="captain-grid">
            {selectedCaptains.map((captain, index) => (
              <article className="captain-card" key={captain.id}>
                <small>Team {String.fromCharCode(65 + index)}</small>
                <strong>{captain.name}</strong>
                <ScorePill score={captain.score} />
              </article>
            ))}
          </div>
        </div>
      ) : (
        <div className="tool-panel">
          <div className="form-grid">
            {[0, 1, 2].map((index) => (
              <label className="field" key={index}>
                <span>Team {String.fromCharCode(65 + index)} captain</span>
                <select
                  className="input"
                  value={manualCaptainIds[index] ?? ""}
                  onChange={(event) => updateManualCaptain(index, event.target.value)}
                >
                  <option value="">Chọn đại diện</option>
                  {eligiblePlayers.map((player) => (
                    <option key={player.id} value={player.id}>
                      {player.name} - {formatScore(player.score)} điểm
                    </option>
                  ))}
                </select>
              </label>
            ))}
          </div>
        </div>
      )}

      <div className={`notice notice-${balance.tone}`}>
        <ShieldAlert size={18} aria-hidden="true" />
        <div>
          <strong>{balance.label}</strong>
          <span>
            {balance.message} Chênh lệch: {formatScore(balance.difference)} điểm.
          </span>
        </div>
      </div>

      {hasDuplicates && (
        <div className="notice notice-danger">
          Không thể chọn trùng một người làm đại diện cho nhiều team.
        </div>
      )}

      <div className="action-footer">
        <StatusBadge tone={balance.tone} label={balance.label} />
        <button
          className="button-primary"
          type="button"
          onClick={() => onConfirm(activeCaptainIds)}
          disabled={!canConfirm}
        >
          <CheckCircle2 size={18} aria-hidden="true" />
          Xác nhận đại diện
        </button>
      </div>
    </section>
  );
}
