import type { ReactNode } from "react";
import type { Level, Role } from "../types/player";
import { levelLabels, roleLabels } from "../data/mockData";
import { formatScore } from "../logic/scoring";

type Tone = "neutral" | "good" | "warn" | "danger" | "sky" | "violet" | "orange";

export function Badge({
  children,
  tone = "neutral",
}: {
  children: ReactNode;
  tone?: Tone;
}) {
  return <span className={`badge badge-${tone}`}>{children}</span>;
}

export function RoleBadge({ role }: { role: Role }) {
  const tone: Tone =
    role === "Attack"
      ? "orange"
      : role === "Defense"
        ? "sky"
        : role === "Setter"
          ? "violet"
          : role === "Full stack"
            ? "good"
            : "neutral";

  return <Badge tone={tone}>{roleLabels[role]}</Badge>;
}

export function LevelBadge({ level }: { level: Level }) {
  const tone: Tone = level === "Good" ? "good" : level === "Average" ? "sky" : "warn";
  return <Badge tone={tone}>{levelLabels[level]}</Badge>;
}

export function ScorePill({ score, label = "điểm" }: { score: number; label?: string }) {
  return (
    <span className="score-pill">
      {formatScore(score)} {label}
    </span>
  );
}

export function StatusBadge({ tone, label }: { tone: "good" | "warn" | "danger"; label: string }) {
  return <Badge tone={tone}>{label}</Badge>;
}
