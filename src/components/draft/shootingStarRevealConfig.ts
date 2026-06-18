import type { RevealRarity } from "../../lib/revealRarity";

export type TwinkleStarSetting = {
  left: string;
  top: string;
  size: string;
  delay: string;
  duration: string;
  opacity: string;
};

export type FeaturedStarSetting = {
  left: string;
  top: string;
  size: string;
  delay: string;
  duration: string;
  opacity: string;
};

export type StreakSetting = {
  left: string;
  top: string;
  delay: string;
  duration: string;
  width: string;
  thickness: string;
  opacity: string;
  angle: string;
  color: string;
  glow: string;
  soft: string;
  blur: string;
  startX: string;
  startY: string;
  endX: string;
  endY: string;
};

export type BurstStarSetting = {
  left: string;
  top: string;
  size: string;
  delay: string;
  duration: string;
};

export const shootingRevealTiming = {
  starFlightDelayMs: 950,
  flashDelayMs: 4650,
  resultDelayMs: 5250,
  reducedFlashDelayMs: 120,
  reducedResultDelayMs: 360,
};

export const shootingCometSettings = {
  anchorRight: "44%",
  anchorTop: "38%",
  width: "min(68vw, 760px)",
  duration: "3600ms",
  angle: "-35deg",
  startX: "32vw",
  startY: "-28vh",
  startScale: "0.42",
  middleX: "8vw",
  middleY: "-5vh",
  middleScale: "1.06",
  lateX: "-12vw",
  lateY: "9vh",
  lateScale: "1.18",
  endX: "-42vw",
  endY: "34vh",
  endScale: "0.78",
};

export const rarityVisuals: Record<
  RevealRarity,
  {
    label: string;
    textClass: string;
    borderClass: string;
    flash: string;
    resultGlow: string;
    core: string;
    glow: string;
    trail: string;
    trailSoft: string;
    streak: string;
  }
> = {
  blue: {
    label: "Blue",
    textClass: "text-sky-200",
    borderClass: "border-sky-300/70",
    flash: "rgba(56, 189, 248, 0.86)",
    resultGlow: "rgba(56, 189, 248, 0.34)",
    core: "#38bdf8",
    glow: "rgba(56, 189, 248, 0.85)",
    trail: "rgba(14, 165, 233, 0.65)",
    trailSoft: "rgba(125, 211, 252, 0.20)",
    streak: "rgba(125, 211, 252, 0.52)",
  },
  purple: {
    label: "Purple",
    textClass: "text-purple-200",
    borderClass: "border-purple-300/70",
    flash: "rgba(168, 85, 247, 0.86)",
    resultGlow: "rgba(168, 85, 247, 0.34)",
    core: "#a855f7",
    glow: "rgba(168, 85, 247, 0.85)",
    trail: "rgba(147, 51, 234, 0.65)",
    trailSoft: "rgba(216, 180, 254, 0.20)",
    streak: "rgba(196, 181, 253, 0.50)",
  },
  gold: {
    label: "Gold",
    textClass: "text-yellow-200",
    borderClass: "border-yellow-300/80",
    flash: "rgba(250, 204, 21, 0.90)",
    resultGlow: "rgba(250, 204, 21, 0.38)",
    core: "#facc15",
    glow: "rgba(250, 204, 21, 0.90)",
    trail: "rgba(245, 158, 11, 0.75)",
    trailSoft: "rgba(253, 224, 71, 0.24)",
    streak: "rgba(253, 224, 71, 0.58)",
  },
};

export const featuredStars: FeaturedStarSetting[] = [
  { left: "8%", top: "9%", size: "16px", delay: "900ms", duration: "2300ms", opacity: "0.95" },
  { left: "22%", top: "17%", size: "11px", delay: "1300ms", duration: "2500ms", opacity: "0.78" },
  { left: "72%", top: "13%", size: "12px", delay: "1500ms", duration: "2400ms", opacity: "0.82" },
  { left: "88%", top: "25%", size: "10px", delay: "1900ms", duration: "2200ms", opacity: "0.70" },
  { left: "14%", top: "43%", size: "12px", delay: "2300ms", duration: "2600ms", opacity: "0.88" },
  { left: "62%", top: "42%", size: "15px", delay: "2600ms", duration: "2500ms", opacity: "0.92" },
  { left: "36%", top: "67%", size: "10px", delay: "2900ms", duration: "2400ms", opacity: "0.76" },
  { left: "79%", top: "73%", size: "13px", delay: "3200ms", duration: "2300ms", opacity: "0.82" },
  { left: "10%", top: "8%", size: "18px", delay: "900ms", duration: "2400ms", opacity: "0.95" },
];

const neonStreakPalette = [
  {
    color: "rgba(14, 165, 233, 0.95)",
    glow: "rgba(14, 165, 233, 0.65)",
    soft: "rgba(14, 165, 233, 0.16)",
  },
  {
    color: "rgba(37, 99, 235, 0.94)",
    glow: "rgba(37, 99, 235, 0.62)",
    soft: "rgba(37, 99, 235, 0.14)",
  },
  {
    color: "rgba(124, 58, 237, 0.92)",
    glow: "rgba(124, 58, 237, 0.60)",
    soft: "rgba(124, 58, 237, 0.13)",
  },
  {
    color: "rgba(45, 212, 191, 0.88)",
    glow: "rgba(45, 212, 191, 0.50)",
    soft: "rgba(45, 212, 191, 0.12)",
  },
];

export const backgroundStreaks: StreakSetting[] = Array.from({ length: 18 }, (_, index) => {
  const palette = neonStreakPalette[index % neonStreakPalette.length];
  const isHeroStreak = index % 13 === 0;

  return {
    left: `${-18 + ((index * 29) % 140)}%`,
    top: `${-16 + ((index * 17) % 132)}%`,
    delay: `${index * 92 - 2800}ms`,
    duration: `${2600 + (index % 6) * 320}ms`,
    width: `${isHeroStreak ? 420 + (index % 3) * 90 : 170 + (index % 5) * 64}px`,
    thickness: `${isHeroStreak ? 2.6 : 1.1 + (index % 3) * 0.45}px`,
    opacity: `${isHeroStreak ? 0.42 : 0.16 + (index % 4) * 0.045}`,
    angle: `${-36 + (index % 5) * 2}deg`,
    blur: `${isHeroStreak ? 12 : 5 + (index % 4) * 2}px`,
    startX: `${20 + (index % 4) * 5}vw`,
    startY: `${-18 - (index % 3) * 3}vh`,
    endX: `${-30 - (index % 5) * 6}vw`,
    endY: `${20 + (index % 4) * 5}vh`,
    ...palette,
  };
});

export const liteBackgroundStreaks: StreakSetting[] = Array.from({ length: 7 }, (_, index) => {
  const palette = neonStreakPalette[index % neonStreakPalette.length];
  const isHeroStreak = index === 0;

  return {
    left: `${-12 + ((index * 37) % 118)}%`,
    top: `${-10 + ((index * 29) % 112)}%`,
    delay: `${index * 210 - 900}ms`,
    duration: `${3000 + (index % 3) * 360}ms`,
    width: `${isHeroStreak ? 320 : 150 + (index % 4) * 42}px`,
    thickness: `${isHeroStreak ? 2 : 1 + (index % 2) * 0.35}px`,
    opacity: `${isHeroStreak ? 0.34 : 0.14 + (index % 3) * 0.04}`,
    angle: `${-36 + (index % 3) * 2}deg`,
    blur: "0px",
    startX: `${18 + (index % 3) * 5}vw`,
    startY: `${-14 - (index % 2) * 3}vh`,
    endX: `${-24 - (index % 4) * 5}vw`,
    endY: `${18 + (index % 3) * 4}vh`,
    ...palette,
  };
});

export const twinkleStars: TwinkleStarSetting[] = Array.from({ length: 92 }, (_, index) => ({
  left: `${(index * 37 + 9) % 98}%`,
  top: `${(index * 61 + 7) % 96}%`,
  size: `${2 + (index % 4)}px`,
  delay: `${(index * 173) % 2300}ms`,
  duration: `${1300 + (index % 6) * 260}ms`,
  opacity: `${0.34 + (index % 5) * 0.10}`,
}));

export const liteTwinkleStars: TwinkleStarSetting[] = Array.from({ length: 24 }, (_, index) => ({
  left: `${(index * 41 + 11) % 96}%`,
  top: `${(index * 67 + 8) % 94}%`,
  size: `${2 + (index % 3)}px`,
  delay: `${(index * 230) % 2100}ms`,
  duration: `${1500 + (index % 4) * 320}ms`,
  opacity: `${0.34 + (index % 4) * 0.08}`,
}));

export const liteFeaturedStars: FeaturedStarSetting[] = [
  { left: "12%", top: "13%", size: "13px", delay: "900ms", duration: "2400ms", opacity: "0.84" },
  { left: "76%", top: "24%", size: "11px", delay: "1700ms", duration: "2600ms", opacity: "0.72" },
  { left: "36%", top: "68%", size: "10px", delay: "2500ms", duration: "2500ms", opacity: "0.76" },
];

export const rarityBurstStars: BurstStarSetting[] = Array.from({ length: 18 }, (_, index) => ({
  left: `${12 + ((index * 23) % 78)}%`,
  top: `${9 + ((index * 31) % 78)}%`,
  size: `${7 + (index % 5) * 2}px`,
  delay: `${900 + index * 135}ms`,
  duration: `${1650 + (index % 4) * 220}ms`,
}));

export const liteRarityBurstStars: BurstStarSetting[] = Array.from({ length: 6 }, (_, index) => ({
  left: `${18 + ((index * 19) % 64)}%`,
  top: `${16 + ((index * 27) % 64)}%`,
  size: `${7 + (index % 3) * 2}px`,
  delay: `${1050 + index * 230}ms`,
  duration: `${1550 + (index % 3) * 180}ms`,
}));
