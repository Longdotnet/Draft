# BLIND_BAG_CARDS_SKILL.md

## Goal

Integrate the `GlowCard` / `spotlight-card` component and use it as the visual UI for the 3 blind bag options in the Volley Draft MVP.

This component should be used in the Blind Bag Draft screen, where the current captain opens one blind bag for their team.

## Current MVP flow

The app uses a one-device live draft flow.

The organizer/admin uses one phone at the volleyball court.

Captains do not log in from their own devices.

When it is a captain's turn, the admin hands the phone to that captain. The captain taps one blind bag, then returns the phone. The app moves to the next captain.

Flow:

```text
1. Admin opens saved match session.
2. App selects 3 balanced captains OR admin manually selects captains.
3. Each team starts with one captain.
4. The remaining players/slots are grouped into 5 draft rounds.
5. Each round has 3 blind bags.
6. Current captain opens one blind bag.
7. App reveals the hidden player/slot.
8. App assigns that slot to the current captain's team.
9. App moves to the next team/captain.
10. After 5 rounds, each team has captain + 5 drafted slots.
```

## Where to use GlowCard

Use `GlowCard` only for the blind bag cards inside the Blind Bag Draft screen.

Do not use this component for every card in the app.

Recommended file:

```text
src/components/draft/BlindBagCard.tsx
```

This component should internally use:

```tsx
import { GlowCard } from "@/components/ui/spotlight-card";
```

## Component integration

Copy the provided component into:

```text
src/components/ui/spotlight-card.tsx
```

If the project uses a different alias instead of `@/`, adjust imports accordingly.

The component exports:

```tsx
export { GlowCard }
```

## BlindBagCard requirements

Create a wrapper component:

```text
src/components/draft/BlindBagCard.tsx
```

Props:

```ts
type BlindBagCardProps = {
  bagNumber: number;
  isOpened: boolean;
  isDisabled: boolean;
  revealedName?: string;
  revealedRole?: string;
  revealedScore?: number;
  onOpen: () => void;
};
```

Behavior:

```text
If the bag is not opened:
- Show gift icon 🎁
- Show "Túi mù 1", "Túi mù 2", or "Túi mù 3"
- Show helper text: "Chạm để khui"
- Card is clickable only if isDisabled is false

If the bag is opened:
- Show revealed player/slot name
- Show role
- Show score
- Show text: "Đã khui"
- Disable further click

If isDisabled is true:
- Reduce opacity
- Disable pointer events
- Show text: "Chưa tới lượt" or "Đã bốc vòng này"
```

## Visual direction

The blind bag cards should feel like a fun mini game but still clean and sporty.

Use:

```text
Dark background
Glow border
Large gift icon
Rounded card
Hover/tap animation
Clear disabled state
Clear opened state
```

Recommended glow colors:

```text
Bag 1: blue
Bag 2: purple
Bag 3: orange
```

## Blind Bag Draft screen layout

In the draft screen, show:

```text
Đại hội bốc thăm túi mù
Vòng bốc: 1 / 5
Lượt hiện tại: Team A
Đại diện: Nick
Nick, hãy khui 1 túi cho Team A.
```

Then show 3 GlowCard blind bags:

```text
🎁 Túi mù 1
🎁 Túi mù 2
🎁 Túi mù 3
```

After captain taps one bag, reveal:

```text
Nick đã khui túi và bốc được An cho Team A.
```

Then show button:

```text
Tiếp tục lượt Team B
```

or automatically move after a short delay if existing flow already supports it.

## Important wording

Use:

```text
Khui túi
Bốc được
Được xếp vào Team A
Lượt hiện tại
Đại diện
```

Avoid:

```text
Chọn người
Nick chọn An
Selected player
```

Reason:

The captain is opening a random blind bag, not manually choosing a player.

## Interaction rules

Only the current captain's turn should allow bag click.

Because this is one-device MVP, there is no backend permission.

Use local UI state:

```text
currentRoundIndex
currentTeamIndex
currentCaptain
openedBags
teams
```

When a bag is clicked:

```text
1. Reveal hidden slot/player inside that bag.
2. Add that slot/player to the current team.
3. Mark bag as opened.
4. Show reveal message.
5. Move to next team.
6. If all 3 teams picked in the round, move to next round.
7. If all 5 rounds are done, show team result.
```

## Responsive behavior

Desktop:

```text
Show 3 blind bag cards in a row.
```

Mobile:

```text
Show 3 blind bag cards stacked vertically or in a swipe-friendly grid.
Cards must be large enough to tap easily.
```

Minimum mobile tap target:

```text
44px
```

## Technical notes

The provided `GlowCard` uses `document.addEventListener('pointermove')`.

This is okay for the draft screen.

Make sure to avoid rendering too many GlowCards across the whole app.

Only render 3 cards at a time in the blind bag area.

## Acceptance criteria

This task is complete when:

```text
1. `GlowCard` is added to `src/components/ui/spotlight-card.tsx`.
2. `BlindBagCard` is created using `GlowCard`.
3. The Blind Bag Draft screen displays exactly 3 blind bag cards per round.
4. The current captain can tap one bag.
5. The selected bag reveals the hidden player/slot.
6. The revealed player/slot is assigned to the current team.
7. The app moves to the next captain/team.
8. Opened bags cannot be clicked again.
9. UI works on mobile.
10. Vietnamese wording is used correctly.
```
