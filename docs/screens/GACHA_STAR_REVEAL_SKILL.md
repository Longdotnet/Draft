# GACHA_STAR_REVEAL_SKILL.md

## Goal

Create a Genshin-inspired gacha star reveal sequence for the Volley Draft blind bag screen.

When the current captain taps a blind bag, the app should show a cinematic reveal screen:

```text
Tap blind bag
↓
Determine hidden player/slot
↓
Calculate rarity from score
↓
Show animated shader background
↓
Show one large flying / glowing star
↓
Star color matches rarity: blue, purple, or gold
↓
Reveal popup appears
↓
User taps Continue
↓
Assign slot to team and move to next captain
```

## Current MVP flow

This is a one-device live draft MVP.

The admin uses one phone at the volleyball court.

When it is a captain's turn, the admin hands the phone to that captain.

The captain taps one blind bag.

No captain login, no multi-device realtime, no SignalR, no room system.

## Rarity mapping

Use the slot/player average score to determine reveal rarity.

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
Blue = New / low score / 1 point
Purple = Average / 2 points
Gold = Good / strong / full stack / 3+ points
```

Examples:

```text
Bảo, score 1 → blue star
Duy, score 2 → purple star
Nick, score 3 → gold star
Sin, score 3.5 → gold star
Bảo / Bình shared slot, average 2 → purple star
```

## Components to create

Create these files:

```text
src/components/draft/GachaStarRevealModal.tsx
src/components/draft/GachaStar.tsx
src/lib/revealRarity.ts
```

## GachaStarRevealModal props

```ts
type RevealRarity = "blue" | "purple" | "gold";

type RevealedSlot = {
  id: string;
  displayName: string;
  role: string;
  averageScore: number;
  type: "single" | "shared";
};

type GachaStarRevealModalProps = {
  isOpen: boolean;
  rarity: RevealRarity;
  revealedSlot: RevealedSlot | null;
  captainName: string;
  teamName: string;
  onContinue: () => void;
  onClose?: () => void;
};
```

## Reveal sequence

The modal should run in phases.

```ts
type RevealPhase =
  | "charging"
  | "star-flight"
  | "flash"
  | "result";
```

Recommended timing:

```text
0ms - 700ms:
charging phase
background darkens, particles/glow build up

700ms - 1700ms:
star-flight phase
one large star flies/zooms from center or top-right to center

1700ms - 2100ms:
flash phase
screen flashes with rarity color

2100ms+:
result phase
show result popup
```

## Visual style

The reveal should feel like a premium gacha / wish animation.

Use:

```text
Animated shader background
One large glowing star
Particle dots / sparkles
Radial glow
Screen flash
Rarity-based color
Result card popup
```

Do not copy exact Genshin assets.

Use the idea only:

```text
cinematic star reveal
rarity color
dramatic result popup
```

## Rarity colors

Use these color themes:

### Blue

```text
Main: #38bdf8
Glow: rgba(56, 189, 248, 0.65)
Text: text-sky-200
Border: border-sky-300
```

### Purple

```text
Main: #a855f7
Glow: rgba(168, 85, 247, 0.7)
Text: text-purple-200
Border: border-purple-300
```

### Gold

```text
Main: #facc15
Glow: rgba(250, 204, 21, 0.85)
Text: text-yellow-200
Border: border-yellow-300
```

## GachaStar component

Create `GachaStar.tsx`.

It should render a big star using CSS or lucide icon.

Preferred:

```tsx
import { Star } from "lucide-react";
```

The star should:

```text
Start small
Move/zoom into center
Rotate slightly
Glow strongly
Change color based on rarity
Trigger screen flash before result appears
```

Mobile-friendly:

```text
Do not rely on hover
Use CSS keyframes
Use transform and opacity
Keep animation smooth
```

## Suggested CSS animations

Add these to the relevant CSS file if not using Tailwind arbitrary animations:

```css
@keyframes gacha-star-flight {
  0% {
    opacity: 0;
    transform: translate3d(40vw, -30vh, 0) scale(0.25) rotate(-20deg);
    filter: blur(8px);
  }
  30% {
    opacity: 1;
    filter: blur(0);
  }
  75% {
    transform: translate3d(0, 0, 0) scale(1.35) rotate(8deg);
  }
  100% {
    opacity: 1;
    transform: translate3d(0, 0, 0) scale(1) rotate(0deg);
  }
}

@keyframes gacha-screen-flash {
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

@keyframes gacha-result-pop {
  0% {
    opacity: 0;
    transform: scale(0.9) translateY(18px);
  }
  100% {
    opacity: 1;
    transform: scale(1) translateY(0);
  }
}

@keyframes gacha-pulse-glow {
  0%, 100% {
    opacity: 0.6;
    transform: scale(1);
  }
  50% {
    opacity: 1;
    transform: scale(1.08);
  }
}
```

## Result popup

When reveal phase becomes `result`, show:

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

Do not assign the revealed slot to the team immediately when the bag is clicked.

Use this flow:

```text
Click bag
↓
Store pending revealed slot
↓
Show gacha star reveal modal
↓
After animation, show result popup
↓
User taps Continue
↓
Assign slot to current team
↓
Move to next captain/team
```

This prevents the user from missing the reveal.

## How to integrate with BlindBagCard

In the blind bag draft screen:

```text
1. User taps a blind bag.
2. Read the hidden slot from that bag.
3. Calculate rarity using averageScore.
4. Set pending reveal data.
5. Open GachaStarRevealModal.
6. When user taps Continue:
   - mark bag opened
   - assign slot to current team
   - clear pending reveal
   - move to next turn
```

Pseudo-code:

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

function handleRevealContinue() {
  if (!pendingReveal) return;

  assignSlotToTeam(pendingReveal.bagId, pendingReveal.slot, currentTeam.id);
  moveToNextTurn();

  setRevealOpen(false);
  setPendingReveal(null);
}
```

## Fallback

If WebGL shader fails or device is weak:

```text
Use a normal CSS gradient background.
Still show the star animation and result popup.
```

Do not block the draft if shader fails.

## Accessibility

Respect reduced motion if possible.

If user prefers reduced motion:

```text
Shorten animation
Skip star flight
Show result popup faster
```

## Acceptance criteria

This task is complete when:

```text
1. Tapping a blind bag opens a full-screen gacha reveal modal.
2. The modal uses AnimatedShaderBackground.
3. A large star appears and animates into view.
4. Star color is blue, purple, or gold based on averageScore.
5. Result popup appears after the star animation.
6. User taps Continue before the app assigns the slot and moves to next turn.
7. Shared slots reveal correctly.
8. The animation works on mobile.
9. The draft flow remains one-device only.
10. No login, realtime, or multi-device permission is added for this task.
```
