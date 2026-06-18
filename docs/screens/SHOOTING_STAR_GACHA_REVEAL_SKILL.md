# SHOOTING_STAR_GACHA_REVEAL_SKILL.md

## Goal

Update the blind bag reveal animation to look like a cinematic shooting-star gacha reveal.

The current implementation is wrong if it only shows a big static star in the center.

The desired animation should feel like a gacha wish / shooting star reveal:

```text
Blind bag tapped
↓
Screen turns into a dark cinematic sky
↓
Fast light streaks fly diagonally across the screen
↓
One main shooting star appears and flies across the screen
↓
The main star color depends on the revealed slot score: blue, purple, or gold
↓
A bright flash happens near the end
↓
Then the reveal popup appears with the player/slot name
```

## Important visual reference

The desired visual direction is closer to:

```text
Dark background
Diagonal glowing light streaks
One main comet / shooting star
Fast cinematic motion
Rarity color trail
```

Not this:

```text
A simple star icon standing still in the center
Small falling rectangles
Static mobile loading screen
```

## Naming

Use these names in code:

```text
ShootingStarRevealModal
ShootingStarTrail
GachaRevealPhase
RevealRarity
```

Do not call the final animation only `GachaStar`, because that makes Codex create a static star icon.

## Rarity mapping

Use averageScore of the hidden slot/player:

```ts
export type RevealRarity = "blue" | "purple" | "gold";

export function getRevealRarity(score: number): RevealRarity {
  if (score >= 3) return "gold";
  if (score >= 2) return "purple";
  return "blue";
}
```

Meaning:

```text
Blue = 1 point / New
Purple = 2 points / Average
Gold = 3+ points / Good / Full stack
```

Examples:

```text
Bảo, score 1 → blue shooting star
Duy, score 2 → purple shooting star
Nick, score 3 → gold shooting star
Sin, score 3.5 → gold shooting star
Bảo / Bình shared slot, average 2 → purple shooting star
```

## Components to create

Create:

```text
src/components/draft/ShootingStarRevealModal.tsx
src/components/draft/ShootingStarTrail.tsx
src/lib/revealRarity.ts
```

Use the existing shader background component:

```text
src/components/ui/animated-shader-background.tsx
```

But the shader background is only the background.

The main reveal animation must be built in `ShootingStarTrail.tsx`.

## Reveal sequence

Use these phases:

```ts
type GachaRevealPhase =
  | "charging"
  | "star-flight"
  | "flash"
  | "result";
```

Timing:

```text
0ms - 500ms:
Charging phase.
Dark background, subtle moving streaks, small glow build-up.

500ms - 1800ms:
Star flight phase.
One main shooting star flies diagonally across the screen.
It should move fast, leaving a long glowing trail.

1800ms - 2200ms:
Flash phase.
Screen flashes using the rarity color.

2200ms+:
Result phase.
Show popup with revealed player/slot.
```

## Main shooting star behavior

The main star should:

```text
Start off-screen from top-right or upper area
Fly diagonally toward lower-left or center
Leave a long glowing trail
Use rarity color: blue / purple / gold
Scale up slightly near the middle
Trigger a bright flash near the end
Disappear before the result popup appears
```

The animation should feel fast and cinematic.

Do not place a large static star in the center.

## CSS animation requirement

Add CSS keyframes for a shooting star.

Example:

```css
@keyframes shooting-star-flight {
  0% {
    opacity: 0;
    transform: translate3d(45vw, -35vh, 0) scale(0.4) rotate(-35deg);
    filter: blur(6px);
  }

  12% {
    opacity: 1;
    filter: blur(0);
  }

  55% {
    opacity: 1;
    transform: translate3d(0vw, 0vh, 0) scale(1.15) rotate(-35deg);
  }

  100% {
    opacity: 0;
    transform: translate3d(-55vw, 45vh, 0) scale(0.75) rotate(-35deg);
    filter: blur(2px);
  }
}

@keyframes shooting-star-trail-pulse {
  0%, 100% {
    opacity: 0.55;
    transform: scaleX(0.95);
  }

  50% {
    opacity: 1;
    transform: scaleX(1.08);
  }
}

@keyframes rarity-screen-flash {
  0% {
    opacity: 0;
  }

  35% {
    opacity: 0.85;
  }

  100% {
    opacity: 0;
  }
}

@keyframes reveal-result-pop {
  0% {
    opacity: 0;
    transform: translateY(18px) scale(0.92);
  }

  100% {
    opacity: 1;
    transform: translateY(0) scale(1);
  }
}
```

## ShootingStarTrail visual structure

The `ShootingStarTrail` component should render:

```text
1. Main star core
2. Long glowing trail behind it
3. Soft outer glow
4. Several smaller background streaks
```

Use divs and CSS gradients instead of a simple icon.

Suggested structure:

```tsx
<div className="shooting-star-wrapper">
  <div className="shooting-star-trail" />
  <div className="shooting-star-core" />
</div>
```

The trail should look like a comet:

```css
background: linear-gradient(
  90deg,
  transparent,
  rgba(..., 0.15),
  rgba(..., 0.85),
  white
);
```

The core should be bright:

```css
box-shadow:
  0 0 20px rarityColor,
  0 0 60px rarityColor,
  0 0 120px rarityColor;
```

## Rarity colors

Use these colors.

### Blue

```text
Core: #38bdf8
Glow: rgba(56, 189, 248, 0.85)
Trail: rgba(14, 165, 233, 0.65)
```

### Purple

```text
Core: #a855f7
Glow: rgba(168, 85, 247, 0.85)
Trail: rgba(147, 51, 234, 0.65)
```

### Gold

```text
Core: #facc15
Glow: rgba(250, 204, 21, 0.9)
Trail: rgba(245, 158, 11, 0.75)
```

## Background streaks

During charging and star-flight phases, show multiple diagonal streaks in the background.

They should:

```text
Move diagonally from top-right to bottom-left
Be thin and fast
Have low opacity
Use mixed blue/purple colors
Become stronger for gold rarity
```

Create 8–14 small streaks using CSS, not heavy Three.js.

Do not make them look like falling rectangles.

They should look like light trails.

## Modal layout

`ShootingStarRevealModal` should be fullscreen:

```text
fixed inset-0 z-[9999]
black background
animated shader background
shooting star layer
flash layer
result popup layer
```

Layer order:

```text
1. Black base background
2. AnimatedShaderBackground
3. Background streaks
4. Main ShootingStarTrail
5. Flash overlay
6. Result popup
```

## Result popup

The result popup is already okay.

Keep the popup simple and clear.

For single slot:

```text
Nick đã bốc được An cho Team A.
Công · 2 điểm
```

For shared slot:

```text
Nick đã bốc được Bảo / Bình 🔁 cho Team A.
Slot thay phiên · Avg 2 điểm
```

Button:

```text
Tiếp tục
```

## Important state rule

Do not reveal the player name immediately when the blind bag is tapped.

Correct flow:

```text
Tap blind bag
↓
Determine hidden slot internally
↓
Calculate rarity
↓
Open shooting star reveal modal
↓
Play shooting star animation
↓
Show flash
↓
Show result popup
↓
User taps Continue
↓
Assign slot to team
↓
Move to next captain
```

## Integration with BlindBagDraft

When a bag is clicked:

```tsx
function handleBagClick(bagId: string) {
  const bag = currentRound.bags.find((x) => x.id === bagId);
  if (!bag || bag.isOpened) return;

  const slot = bag.hiddenSlot;
  const rarity = getRevealRarity(slot.averageScore);

  setPendingReveal({
    bagId,
    slot,
    rarity,
    captainName: currentCaptain.name,
    teamName: currentTeam.name,
  });

  setRevealOpen(true);
}
```

When continue is clicked:

```tsx
function handleRevealContinue() {
  if (!pendingReveal) return;

  assignSlotToTeam(pendingReveal.bagId, pendingReveal.slot, currentTeam.id);
  markBagOpened(pendingReveal.bagId);
  moveToNextTurn();

  setRevealOpen(false);
  setPendingReveal(null);
}
```

## Mobile requirements

The animation must work on mobile.

Use CSS transform animations.

Do not rely on hover.

Do not require pointer movement.

Keep animation around 2.2 seconds.

Avoid rendering too many Three.js scenes.

Only mount the shader background while reveal modal is open.

## Reduced motion

If user prefers reduced motion:

```text
Skip star-flight phase or shorten it.
Show a simple rarity flash.
Then show result popup.
```

## Acceptance criteria

This task is complete when:

```text
1. Tapping a blind bag opens a fullscreen cinematic reveal modal.
2. The modal uses the animated shader background.
3. The main animation is a flying shooting star / comet trail, not a static star icon.
4. The star trail color is blue, purple, or gold based on averageScore.
5. Background has diagonal light streaks like a gacha wish scene.
6. The result popup appears only after the animation finishes.
7. User taps "Tiếp tục" before the app assigns the slot and moves to next captain.
8. The animation works on mobile.
9. Existing team assignment and draft logic still works.
10. No login, realtime, or multi-device feature is added in this task.
```
