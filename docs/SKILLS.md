# SKILLS.md

## Product skill

You are building an MVP, not a full production app.

Prioritize:

```text
Clear user flow
Readable UI
Simple data model
Fast implementation
Easy future refactor
```

Do not over-engineer.

Avoid:

```text
Backend
Authentication
Database
Realtime
Complex permissions
Payment
Invite links
```

for the first version.

## Frontend skill

Use:

```text
React
Vite
TypeScript
Tailwind CSS
Component-based structure
Local state first
```

Suggested folder structure:

```text
src/
  components/
    PlayerList.tsx
    SharedSlotSetup.tsx
    CaptainSelection.tsx
    BlindBagDraft.tsx
    TeamResult.tsx
    SetRotation.tsx
    BalanceCheck.tsx

  data/
    mockData.ts

  types/
    player.ts
    slot.ts
    team.ts

  logic/
    scoring.ts
    captainSelection.ts
    draftRounds.ts
    balanceCheck.ts
    rotation.ts

  App.tsx
```

## Data modeling skill

Use these main types:

```ts
export type Role =
  | "Attack"
  | "Defense"
  | "Setter"
  | "Full stack"
  | "New";

export type Level =
  | "Good"
  | "Average"
  | "New";

export type Player = {
  id: string;
  name: string;
  role: Role;
  level: Level;
  score: number;
};

export type SlotType = "single" | "shared";

export type DraftSlot = {
  id: string;
  type: SlotType;
  displayName: string;
  playerIds: string[];
  role: Role;
  averageScore: number;
  isCaptainEligible: boolean;
};

export type Team = {
  id: string;
  name: string;
  captainSlotId?: string;
  slots: DraftSlot[];
};

export type RotationEntry = {
  setNumber: number;
  teamId: string;
  slotId: string;
  playerId: string;
  playerName: string;
  score: number;
};

export type DraftRound = {
  id: string;
  roundNumber: number;
  label: string;
  slots: DraftSlot[];
};
```

## Scoring skill

Implement score mapping:

```ts
Good = 3
Average = 2
New = 1
Full stack bonus = 0.5
```

For normal players:

```ts
score = level score + role bonus if role is Full stack
```

For shared slots:

```ts
averageScore = average(player.score)
```

For balance:

```text
Average team score = sum of averageScore of all slots
Set team score = sum of actual player score for that set
```

## Captain selection skill

There are two modes:

```text
Auto balanced
Manual
```

Auto balanced approach:

```text
1. Filter eligible players.
2. Exclude players inside shared slots.
3. Group/sort by score.
4. Randomly select 3 players whose score difference is acceptable.
5. Prefer captain score difference <= 0.5.
6. If not possible, allow difference <= 1.
7. Show warning if difference > 1.
```

Manual approach:

```text
Admin chooses one captain for each team.
Validate:
- No duplicate captain
- Captain must be single player
- Captain must not be inside shared slot
- Show balance warning
```

## Draft round skill

After captains are selected:

```text
1. Convert remaining players/shared slots into draft slots.
2. Remove captain slots.
3. Create 5 rounds for 3 teams.
4. Each round has 3 slots.
5. Slots inside the same round should have similar average scores.
```

Simple implementation:

```text
Sort remaining slots by averageScore descending.
Chunk them into groups of 3.
Each group is one draft round.
Shuffle inside each round so blind bags feel random.
```

Example:

```text
Round 1: 3.5, 3, 3
Round 2: 2.5, 2, 2
Round 3: 2, 2, 1.5
Round 4: 1, 1, 1
Round 5: mixed
```

## Blind bag draft skill

Blind bag draft should be turn-based.

State:

```text
currentRoundIndex
currentTeamIndex
openedBags
teams
```

Flow:

```text
1. Show current round.
2. Show current team.
3. Show current captain.
4. Show unopened bags.
5. Captain clicks a bag.
6. Reveal hidden slot.
7. Add slot to current team.
8. Move to next team.
9. If all teams have picked in current round, move to next round.
10. End when all rounds complete.
```

Use wording:

```text
Nick đã khui túi và bốc được An cho Team A.
```

Do not use:

```text
Nick đã chọn An.
```

## Shared slot skill

Shared slot means multiple players share one team position.

Rules:

```text
Shared slot counts as one slot in team size.
Shared slot can be drafted like a normal slot.
Shared slot shows 🔁 badge.
Shared slot has average score.
Shared slot has set-by-set rotation.
```

For MVP, use round-robin rotation:

```text
If slot has Bảo and Bình:
Set 1: Bảo
Set 2: Bình
Set 3: Bảo
Set 4: Bình
```

If slot has 3 players:

```text
Set 1: Player A
Set 2: Player B
Set 3: Player C
Set 4: Player A
```

## Balance skill

Show two kinds of balance.

### Average balance

```text
Use averageScore of each slot.
Compare total score of Team A, Team B, Team C.
```

Status:

```text
Difference <= 1: Cân bằng tốt
Difference > 1 and <= 2: Hơi lệch
Difference > 2: Lệch mạnh
```

### Set balance

For each set:

```text
Use actual player score in shared slot rotation.
Calculate total score per team per set.
Show difference and status.
```

Example:

```text
Set 1:
Team A = 8
Team B = 8.5
Team C = 8.5
Status = Cân bằng tốt

Set 2:
Team A = 10
Team B = 8.5
Team C = 8.5
Status = Hơi lệch
```

## UI taste skill

The UI should feel like:

```text
A fun live draft event
Sporty
Clean
Mobile friendly
Slightly game-like
Not childish
```

Use sections:

```text
Header
Stepper
Main card
Action cards
Team cards
Balance table
```

Recommended UI language:

```text
Đại hội bốc thăm túi mù
Lượt hiện tại
Đại diện
Vòng bốc
Khui túi
Đã khui được
Được xếp vào
Cân bằng tốt
Hơi lệch
Lệch mạnh
```

## Gemini image integration skill

Use Gemini-generated image/mockup as visual reference only.

Do not copy blindly.

When using image mockups:

```text
1. Extract layout structure.
2. Keep the product flow from INSTRUCTIONS.md.
3. Preserve core concepts: captain, blind bag, shared slot, balance.
4. Improve unclear wording.
5. Keep UI responsive.
```

The Gemini image may show admin opening bags. Update it to captain-based flow:

```text
Current turn: Team A
Captain: Nick
Nick, choose one blind bag for Team A.
```

Instead of:

```text
Admin opens bag for Team A.
```

## Implementation priority

Build in this order:

```text
1. Static UI with mock data
2. Player list screen
3. Captain selection screen
4. Blind bag draft screen
5. Team result screen
6. Shared slot display
7. Set rotation table
8. Balance check
9. Add basic interactions
10. Add localStorage
```
