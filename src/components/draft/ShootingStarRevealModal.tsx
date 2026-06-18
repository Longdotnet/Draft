import { CSSProperties, useEffect, useMemo, useState } from "react";
import type { RevealRarity } from "../../lib/revealRarity";
import AnimatedShaderBackground from "../ui/animated-shader-background";
import { ShootingStarTrail, type GachaRevealPhase } from "./ShootingStarTrail";
import { rarityVisuals, shootingRevealTiming } from "./shootingStarRevealConfig";

type RevealedSlot = {
  id: string;
  displayName: string;
  role: string;
  gender?: string;
  averageScore: number;
  type: "single" | "shared";
};

type ShootingStarRevealModalProps = {
  isOpen: boolean;
  rarity: RevealRarity;
  revealedSlot: RevealedSlot | null;
  captainName: string;
  teamName: string;
  onContinue: () => void;
  onClose?: () => void;
};

function useReducedMotion() {
  const [isReducedMotion, setIsReducedMotion] = useState(false);

  useEffect(() => {
    const query = window.matchMedia("(prefers-reduced-motion: reduce)");
    const update = () => setIsReducedMotion(query.matches);

    update();
    query.addEventListener("change", update);
    return () => query.removeEventListener("change", update);
  }, []);

  return isReducedMotion;
}

export function ShootingStarRevealModal({
  isOpen,
  rarity,
  revealedSlot,
  captainName,
  teamName,
  onContinue,
  onClose,
}: ShootingStarRevealModalProps) {
  const [phase, setPhase] = useState<GachaRevealPhase>("charging");
  const isReducedMotion = useReducedMotion();
  const rarityInfo = rarityVisuals[rarity];
  const scoreLabel = revealedSlot
    ? Number.isInteger(revealedSlot.averageScore)
      ? revealedSlot.averageScore.toFixed(0)
      : revealedSlot.averageScore.toFixed(1)
    : "";
  const slotMeta = useMemo(() => {
    if (!revealedSlot) return "";
    const genderText = revealedSlot.gender ? ` · ${revealedSlot.gender}` : "";
    if (revealedSlot.type === "shared") {
      return `Slot thay phiên${genderText} · Avg ${scoreLabel} điểm`;
    }

    return `${revealedSlot.role}${genderText} · ${scoreLabel} điểm`;
  }, [revealedSlot, scoreLabel]);

  useEffect(() => {
    if (!isOpen) {
      setPhase("charging");
      return;
    }

    setPhase("charging");
    const timings = isReducedMotion
      ? [
          window.setTimeout(() => setPhase("flash"), shootingRevealTiming.reducedFlashDelayMs),
          window.setTimeout(() => setPhase("result"), shootingRevealTiming.reducedResultDelayMs),
        ]
      : [
          window.setTimeout(() => setPhase("star-flight"), shootingRevealTiming.starFlightDelayMs),
          window.setTimeout(() => setPhase("flash"), shootingRevealTiming.flashDelayMs),
          window.setTimeout(() => setPhase("result"), shootingRevealTiming.resultDelayMs),
        ];

    return () => timings.forEach((timer) => window.clearTimeout(timer));
  }, [isOpen, isReducedMotion, revealedSlot?.id]);

  if (!isOpen) {
    return null;
  }

  return (
    <div
      className="fixed inset-0 z-[9999] overflow-hidden bg-black text-white"
      role="dialog"
      aria-modal="true"
      aria-label="Màn hình khui túi mù"
    >
      <AnimatedShaderBackground />
      <div className="absolute inset-0 bg-black/45" />
      <div className="absolute inset-0 bg-[radial-gradient(circle_at_68%_12%,rgba(255,255,255,0.16),transparent_18%),radial-gradient(circle_at_48%_54%,rgba(14,165,233,0.12),transparent_32%)]" />

      <ShootingStarTrail rarity={rarity} phase={phase} isReducedMotion={isReducedMotion} />

      {phase === "flash" && (
        <div
          className="rarity-screen-flash"
          style={{ "--rarity-flash": rarityInfo.flash } as CSSProperties}
        />
      )}

      <div className="relative z-40 flex h-dvh items-center justify-center px-4 py-8">
        <div className="flex w-full max-w-xl flex-col items-center text-center">
          {phase !== "result" && (
            <div className="shooting-star-caption">
              <p className="eyebrow text-sky-200">Đang khui túi mù</p>
              <p>Vị tinh tú đang bay qua bầu trời...</p>
            </div>
          )}

          {phase === "result" && revealedSlot && (
            <div
              className={`shooting-result-card ${rarityInfo.borderClass}`}
              style={{ "--result-glow": rarityInfo.resultGlow } as CSSProperties}
              aria-live="polite"
            >
              <span className={`text-xs font-black uppercase tracking-normal ${rarityInfo.textClass}`}>
                {rarityInfo.label} reveal
              </span>
              <h2>{revealedSlot.displayName}</h2>
              <p>
                {captainName} đã bốc được <strong>{revealedSlot.displayName}</strong> cho{" "}
                {teamName}.
              </p>
              <div className="mt-4 inline-flex rounded-md border border-white/10 bg-white/10 px-3 py-2 text-sm font-black text-white">
                {slotMeta}
              </div>
              <button className="button-primary mt-6 w-full sm:w-auto" type="button" onClick={onContinue}>
                Tiếp tục
              </button>
            </div>
          )}

          {phase === "result" && !revealedSlot && (
            <div
              className={`shooting-result-card ${rarityInfo.borderClass}`}
              style={{ "--result-glow": rarityInfo.resultGlow } as CSSProperties}
            >
              <h2>Không đọc được túi</h2>
              <p>Vui lòng quay lại màn draft và thử lại.</p>
              <button className="button-primary mt-6 w-full sm:w-auto" type="button" onClick={onClose ?? onContinue}>
                Tiếp tục
              </button>
            </div>
          )}
        </div>
      </div>
    </div>
  );
}
