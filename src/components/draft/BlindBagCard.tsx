import { CheckCircle2, Gift, Lock } from "lucide-react";
import { GlowCard } from "../ui/spotlight-card";
import { ScorePill } from "../ui";

type BlindBagCardProps = {
  bagNumber: number;
  isOpened: boolean;
  isDisabled: boolean;
  revealedName?: string;
  revealedRole?: string;
  revealedScore?: number;
  onOpen: () => void;
};

const glowColors = [
  "rgba(56, 189, 248, 0.50)",
  "rgba(167, 139, 250, 0.52)",
  "rgba(251, 146, 60, 0.52)",
];

export function BlindBagCard({
  bagNumber,
  isOpened,
  isDisabled,
  revealedName,
  revealedRole,
  revealedScore,
  onOpen,
}: BlindBagCardProps) {
  const disabled = isOpened || isDisabled;
  const glowColor = glowColors[(bagNumber - 1) % glowColors.length];

  return (
    <button
      type="button"
      className="blind-bag-card-button"
      onClick={onOpen}
      disabled={disabled}
      aria-label={isOpened ? `Túi mù ${bagNumber} đã khui` : `Khui túi mù ${bagNumber}`}
    >
      <GlowCard
        glowColor={glowColor}
        className={[
          "blind-bag-card",
          isOpened ? "blind-bag-card-opened" : "",
          isDisabled && !isOpened ? "blind-bag-card-disabled" : "",
        ].join(" ")}
      >
        {isOpened ? (
          <div className="flex h-full flex-col items-center justify-center gap-3 text-center">
            <div className="blind-bag-icon opened">
              <CheckCircle2 size={32} aria-hidden="true" />
            </div>
            <span className="text-xs font-black uppercase tracking-normal text-emerald-200">
              Đã khui
            </span>
            <strong className="max-w-full text-xl font-black text-white">{revealedName}</strong>
            <div className="flex flex-wrap items-center justify-center gap-2">
              {revealedRole && <span className="blind-bag-chip">{revealedRole}</span>}
              {typeof revealedScore === "number" && <ScorePill score={revealedScore} />}
            </div>
          </div>
        ) : (
          <div className="flex h-full flex-col items-center justify-center gap-3 text-center">
            <div className="blind-bag-icon">
              {isDisabled ? <Lock size={34} aria-hidden="true" /> : <Gift size={38} aria-hidden="true" />}
            </div>
            <strong className="text-2xl font-black text-white">Túi mù {bagNumber}</strong>
            <span className="text-sm font-bold text-slate-300">
              {isDisabled ? "Chưa tới lượt" : "Chạm để khui"}
            </span>
          </div>
        )}
      </GlowCard>
    </button>
  );
}
