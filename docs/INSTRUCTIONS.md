# INSTRUCTIONS.md

## Project name

Volley Draft MVP

## Goal

Build a responsive MVP web app for casual volleyball team drafting.

The app helps a volleyball group create fair teams while keeping the draft fun. It supports:

* Player list management
* Skill-based scoring
* Shared slots where 2 or more players rotate in one playing position
* Captain / representative selection
* Blind bag draft by captains
* Team result display
* Set-by-set rotation
* Balance checking

## Core concept

Do not divide players directly into teams.

Use this flow:

```text
Player → Slot → Captain → Blind Bag Draft → Team → Set Rotation → Balance Check
```

A player is a real person.

A slot is a playing position in the team.

A slot can be:

```text
Single slot = 1 player
Shared slot = 2 or more players rotating by set
```

Example:

```text
Bảo: New player, score 1
Bình: Good player, score 3

Bảo / Bình share one slot.

Average slot score: 2

Set 1: Bảo plays
Set 2: Bình plays
Set 3: Bảo plays
Set 4: Bình plays
```

## MVP flow

The MVP supports 18 players and 3 teams.

Each team has 6 members/slots.

Flow:

```text
1. Admin enters 18 players.
2. Admin optionally creates shared slots.
3. App selects 3 captains or admin manually selects captains.
4. Each team starts with one captain.
5. The remaining players/slots are placed into blind bags.
6. Captains take turns opening blind bags.
7. Each captain gets 5 more slots/players for their team.
8. The app displays final teams.
9. The app displays set rotation for shared slots.
10. The app checks balance by average score and by set score.
```

## Captain selection rules

There are 2 captain selection modes:

### Mode 1: Auto balanced captains

The app randomly selects 3 captains from eligible players.

The captains should have similar scores.

Good example:

```text
Team A captain: Nick, 3 pts
Team B captain: Bình, 3 pts
Team C captain: Minh, 3 pts
```

Bad example:

```text
Team A captain: Sin, 3.5 pts
Team B captain: Bảo, 1 pt
Team C captain: Huy, 1 pt
```

The app should warn when captains are too unbalanced.

### Mode 2: Manual captain selection

Admin can manually choose captains for Team A, Team B, and Team C.

The app should show a captain balance warning if their scores are too far apart.

For MVP:

```text
Captains must be single players.
Players inside a shared slot cannot be selected as captain.
If a player is selected as captain, they cannot be added to a shared slot.
```

## Blind bag draft rules

After captains are confirmed:

```text
Team A starts with Captain A.
Team B starts with Captain B.
Team C starts with Captain C.
```

The remaining 15 slots/players are drafted through blind bags.

Each captain takes turns opening one blind bag for their team.

Important:

```text
The captain only clicks a bag.
The captain does not manually choose the player.
The result is random.
```

Use wording like:

```text
Nick opened a blind bag and got An for Team A.
```

Do not use wording like:

```text
Nick selected An.
```

because that sounds like manual choosing.

## Draft fairness

Blind bags should be grouped by skill tier or pre-balanced rounds.

Preferred MVP logic:

```text
1. Remove captains from draft pool.
2. Convert remaining players/shared slots into draftable slots.
3. Sort/group slots by score.
4. Create 5 draft rounds.
5. Each round contains 3 slots with similar score.
6. Each captain opens one bag in each round.
7. After 5 rounds, each team has 6 slots including captain.
```

Example:

```text
Round 1: high score slots
Round 2: average score slots
Round 3: average score slots
Round 4: low score slots
Round 5: mixed score slots
```

## Scoring

Default score:

```text
Good = 3
Average = 2
New = 1
Full stack bonus = +0.5
```

Example:

```text
Nick - Attack - Good = 3
Sin - Full stack - Good = 3.5
Duy - Setter - Average = 2
Bảo - New - New = 1
```

Shared slot score:

```text
Average score = average of all players in that shared slot
```

Example:

```text
Bảo = 1
Bình = 3

Shared slot average = 2
```

But balance check must also support set-by-set score:

```text
Set 1: Bảo plays → slot score = 1
Set 2: Bình plays → slot score = 3
```

## UI taste

Use a modern, sporty, fun, dark-mode-friendly design.

Visual direction:

```text
Dark navy background
Clean white cards
Rounded corners
Soft shadows
Sporty neon accent
Fun blind bag interaction
Mobile responsive
```

Recommended style:

```text
Background: #070b1a or #0f172a
Card: #ffffff or #111827 depending on section
Accent: orange / blue / violet
Success: green
Warning: amber
Danger: red
```

Use badges:

```text
Attack
Defense
Setter
Full stack
New
Good
Average
New player
Shared slot
Captain
```

Use icons:

```text
🎁 Blind bag
🔁 Shared slot
⚖️ Balance
👑 Captain
🏐 Volleyball
```

## Screens to build

Build these screens/components:

```text
1. Player List
2. Shared Slot Setup
3. Captain Selection
4. Blind Bag Draft
5. Team Result
6. Set Rotation Schedule
7. Balance Check
```

The first version can use static mock data and local state.

No backend required for first MVP.

Use React + Vite + TypeScript.

Use Tailwind CSS if available.

Use localStorage later, but first focus on UI and flow.

## Sample data

```ts
const players = [
  { id: "p1", name: "Nick", role: "Attack", level: "Good", score: 3 },
  { id: "p2", name: "Sin", role: "Full stack", level: "Good", score: 3.5 },
  { id: "p3", name: "Duy", role: "Setter", level: "Average", score: 2 },
  { id: "p4", name: "Long", role: "Defense", level: "Average", score: 2 },
  { id: "p5", name: "Bảo", role: "New", level: "New", score: 1 },
  { id: "p6", name: "Bình", role: "Attack", level: "Good", score: 3 },
  { id: "p7", name: "Nam", role: "Defense", level: "Average", score: 2 },
  { id: "p8", name: "Huy", role: "New", level: "New", score: 1 },
  { id: "p9", name: "An", role: "Attack", level: "Average", score: 2 },
  { id: "p10", name: "Cường", role: "Full stack", level: "Good", score: 3.5 },
  { id: "p11", name: "Minh", role: "Setter", level: "Good", score: 3 },
  { id: "p12", name: "Khoa", role: "Defense", level: "Average", score: 2 }
];
```

## Important UX language

Use Vietnamese UI text.

Preferred Vietnamese terms:

```text
Người chơi
Vai trò
Trình độ
Điểm
Đội
Đại diện
Đội trưởng
Bốc túi mù
Khui túi
Slot thay phiên
Lịch thay phiên
Cân bằng đội
Lượt hiện tại
Vòng bốc
```

Preferred reveal text:

```text
Nick đã khui túi và bốc được An cho Team A.
```

Avoid:

```text
Nick chọn An.
```

because the captain should not appear to manually choose the player.

## Definition of done

The MVP UI is done when:

```text
1. User can see player list.
2. User can see shared slot example Bảo / Bình.
3. User can choose captains by auto or manual mode.
4. User can see blind bag draft screen with current captain turn.
5. User can see team result after draft.
6. User can see set rotation for shared slots.
7. User can see balance check by average score and set score.
8. UI is responsive and clear on mobile.
```
