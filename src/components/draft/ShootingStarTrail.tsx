import { CSSProperties } from "react";
import type { RevealRarity } from "../../lib/revealRarity";
import {
  backgroundStreaks,
  featuredStars,
  rarityBurstStars,
  rarityVisuals,
  shootingCometSettings,
  twinkleStars,
} from "./shootingStarRevealConfig";

export type GachaRevealPhase = "charging" | "star-flight" | "flash" | "result";

type ShootingStarTrailProps = {
  rarity: RevealRarity;
  phase: GachaRevealPhase;
  isReducedMotion?: boolean;
};

export function ShootingStarTrail({
  rarity,
  phase,
  isReducedMotion = false,
}: ShootingStarTrailProps) {
  const colors = rarityVisuals[rarity];
  const showMainStar = phase === "star-flight" || phase === "flash";
  const showStreaks = phase !== "result";

  return (
    <div
      className="shooting-star-scene"
      style={
        {
          "--shooting-core": colors.core,
          "--shooting-glow": colors.glow,
          "--shooting-trail": colors.trail,
          "--shooting-trail-soft": colors.trailSoft,
          "--shooting-streak": colors.streak,
          "--comet-anchor-right": shootingCometSettings.anchorRight,
          "--comet-anchor-top": shootingCometSettings.anchorTop,
          "--comet-width": shootingCometSettings.width,
          "--comet-duration": shootingCometSettings.duration,
          "--comet-angle": shootingCometSettings.angle,
          "--comet-start-x": shootingCometSettings.startX,
          "--comet-start-y": shootingCometSettings.startY,
          "--comet-start-scale": shootingCometSettings.startScale,
          "--comet-middle-x": shootingCometSettings.middleX,
          "--comet-middle-y": shootingCometSettings.middleY,
          "--comet-middle-scale": shootingCometSettings.middleScale,
          "--comet-late-x": shootingCometSettings.lateX,
          "--comet-late-y": shootingCometSettings.lateY,
          "--comet-late-scale": shootingCometSettings.lateScale,
          "--comet-end-x": shootingCometSettings.endX,
          "--comet-end-y": shootingCometSettings.endY,
          "--comet-end-scale": shootingCometSettings.endScale,
        } as CSSProperties
      }
      aria-hidden="true"
    >
      <div className="shooting-star-twinkle-field">
        {twinkleStars.map((star, index) => (
          <span
            className="shooting-star-twinkle"
            key={index}
            style={
              {
                "--twinkle-left": star.left,
                "--twinkle-top": star.top,
                "--twinkle-size": star.size,
                "--twinkle-delay": star.delay,
                "--twinkle-duration": star.duration,
                "--twinkle-opacity": star.opacity,
              } as CSSProperties
            }
          />
        ))}
      </div>

      {showStreaks && (
        <div className="shooting-featured-star-field">
          {featuredStars.map((star, index) => (
            <span
              className="shooting-featured-star"
              key={index}
              style={
                {
                  "--featured-left": star.left,
                  "--featured-top": star.top,
                  "--featured-size": star.size,
                  "--featured-delay": star.delay,
                  "--featured-duration": star.duration,
                  "--featured-opacity": star.opacity,
                } as CSSProperties
              }
            />
          ))}
        </div>
      )}

      {showStreaks && (
        <div className="shooting-star-streak-field">
          {backgroundStreaks.map((streak, index) => (
            <span
              className="shooting-star-streak"
              key={index}
              style={
                {
                  "--shooting-streak-left": streak.left,
                  "--shooting-streak-top": streak.top,
                  "--shooting-streak-delay": streak.delay,
                  "--shooting-streak-duration": streak.duration,
                  "--shooting-streak-width": streak.width,
                  "--shooting-streak-thickness": streak.thickness,
                  "--shooting-streak-opacity": streak.opacity,
                  "--shooting-streak-angle": streak.angle,
                  "--shooting-streak-color": streak.color,
                  "--shooting-streak-glow": streak.glow,
                  "--shooting-streak-soft": streak.soft,
                  "--shooting-streak-blur": streak.blur,
                  "--shooting-streak-start-x": streak.startX,
                  "--shooting-streak-start-y": streak.startY,
                  "--shooting-streak-end-x": streak.endX,
                  "--shooting-streak-end-y": streak.endY,
                } as CSSProperties
              }
            >
              <i className="shooting-star-streak-head" />
            </span>
          ))}
        </div>
      )}

      {showMainStar && !isReducedMotion && (
        <div className="shooting-rarity-burst-field">
          {rarityBurstStars.map((star, index) => (
            <span
              className="shooting-rarity-burst-star"
              key={index}
              style={
                {
                  "--burst-left": star.left,
                  "--burst-top": star.top,
                  "--burst-size": star.size,
                  "--burst-delay": star.delay,
                  "--burst-duration": star.duration,
                } as CSSProperties
              }
            />
          ))}
        </div>
      )}

      {showMainStar && !isReducedMotion && (
        <div className="shooting-star-wrapper">
          <div className="shooting-star-outer-glow" />
          <div className="shooting-star-trail" />
          <div className="shooting-star-core" />
        </div>
      )}
    </div>
  );
}
