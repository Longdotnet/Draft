# ONE_DEVICE_MVP_SCOPE.md

## Project direction

Build Volley Draft MVP as a one-device live drafting app.

The organizer/admin uses one phone at the volleyball court.

Captains do not log in from their own devices.

When it is a captain's turn, the organizer hands the phone to that captain. The captain opens one blind bag, then returns the phone. The app automatically moves to the next captain/team.

## Do not build in version 1

Do not implement these in version 1:

```text
Login
JWT auth
Backend permission
Database
Multi-device session
Realtime
SignalR
Polling
Room code
User roles
Captain account
```

Version 1 should be frontend-only.

Use:

```text
React
Vite
TypeScript
localStorage
```

## Core flow

There are 18 players and 3 teams.

Each team has 6 slots/members.

Flow:

```text
1. Admin enters player list.
2. Admin optionally creates shared slots.
3. App selects 3 balanced captains automatically OR admin manually selects captains.
4. Each captain is assigned to one team.
5. Each team starts with its captain.
6. The remaining players/slots are placed into blind bags.
7. Captains open blind bags one by one using the same phone.
8. After each pick, the app moves to the next captain.
9. After 5 rounds, each team has 6 slots/members.
10. App shows team result, set rotation, and balance check.
```

## Captain selection

There are 2 captain selection modes:

```text
1. Auto balanced captains
2. Manual captain selection
```

### Auto balanced captains

The app randomly selects 3 captains with similar skill scores.

Preferred rule:

```text
Choose 3 players whose score difference is <= 0.5.
If impossible, allow <= 1.
If still impossible, choose the closest group and show warning.
```

Example good captain group:

```text
Team A captain: Nick - 3 pts
Team B captain: Bình - 3 pts
Team C captain: Minh - 3 pts
```

Bad captain group:

```text
Team A captain: Sin - 3.5 pts
Team B captain: Bảo - 1 pt
Team C captain: Huy - 1 pt
```

### Manual captain selection

Admin can manually choose or edit captains.

The app should validate:

```text
No duplicate captains
Captain must be a single player
Captain should not be inside a shared slot
Show warning if captain scores are too far apart
```

## Blind bag draft model

Use turn-based blind bag draft on one device.

Example:

```text
Round 1 / 5
Current turn: Team A
Captain: Nick

Nick, hãy khui 1 túi cho Team A.
```

Admin hands the phone to Nick.

Nick taps one blind bag.

Reveal:

```text
Nick đã khui túi và bốc được An cho Team A.
```

Then app moves to:

```text
Next turn: Team B
Captain: Bình
```

Admin hands the same phone to Bình.

Continue until all rounds are complete.

## Draft rounds

After 3 captains are assigned, there are 15 remaining players/slots.

Create 5 rounds.

Each round has 3 blind bags.

Each team/captain opens one bag per round.

Example:

```text
Round 1:
Team A captain opens 1 bag
Team B captain opens 1 bag
Team C captain opens 1 bag

Round 2:
Team A captain opens 1 bag
Team B captain opens 1 bag
Team C captain opens 1 bag
```

After 5 rounds:

```text
Team A = captain + 5 drafted slots
Team B = captain + 5 drafted slots
Team C = captain + 5 drafted slots
```

## Fairness rule

Blind bags should be grouped into balanced rounds.

Simple MVP algorithm:

```text
1. Remove captains from draft pool.
2. Convert remaining players/shared slots into draft slots.
3. Sort slots by average score descending.
4. Chunk them into groups of 3.
5. Each group becomes one round.
6. Shuffle slots inside each round.
```

This keeps randomness fun but prevents extreme imbalance.

## Shared slot

A shared slot means multiple players share one playing position and rotate by set.

Example:

```text
Bảo = New = 1 point
Bình = Good = 3 points

Shared slot:
Bảo / Bình 🔁
Average score = 2

Set 1: Bảo plays
Set 2: Bình plays
Set 3: Bảo plays
Set 4: Bình plays
```

A shared slot counts as one team slot.

A shared slot can appear inside a blind bag.

Display it as:

```text
Bảo / Bình 🔁
Avg 2 pts
```

Do not split Bảo and Bình into two separate team members.

## Screens to build

Build these screens/components:

```text
1. Player list
2. Shared slot setup
3. Captain selection
4. Blind bag draft
5. Team result
6. Set rotation
7. Balance check
```

## UI wording

Use Vietnamese UI text.

Preferred words:

```text
Người chơi
Vai trò
Trình độ
Điểm
Đại diện
Đội trưởng
Bốc túi mù
Khui túi
Lượt hiện tại
Vòng bốc
Slot thay phiên
Lịch thay phiên
Cân bằng đội
```

Preferred reveal message:

```text
Nick đã khui túi và bốc được An cho Team A.
```

Avoid:

```text
Nick đã chọn An.
```

because the captain does not manually choose the player.

## Storage

Use localStorage for version 1.

Store:

```text
Players
Shared slots
Captain selection
Draft progress
Team result
Rotation
Balance result
```

No backend in version 1.

## Definition of done

The MVP is done when:

```text
1. Admin can add players.
2. Admin can create shared slots.
3. App can auto-select 3 balanced captains.
4. Admin can manually adjust captains.
5. App creates 5 blind bag rounds.
6. Captains open bags turn by turn on one phone.
7. Teams are built correctly.
8. Shared slots are shown correctly.
9. Rotation by set is shown.
10. Balance by average score and by set score is shown.
11. Data persists in localStorage after refresh.
```
