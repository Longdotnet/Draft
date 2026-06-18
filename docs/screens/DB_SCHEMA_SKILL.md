# DB_SCHEMA_SKILL.md

## Goal

Design the database schema for Volley Draft MVP with captain-based blind bag draft and strict permission handling.

Tech stack:

```text
ASP.NET Core Web API
EF Core
SQLite for MVP
JWT authentication
React + Vite + TypeScript frontend
```

## Main entities

Use these entities:

```text
User
MatchSession
SessionPlayer
Team
DraftSlot
DraftSlotPlayer
DraftRound
BlindBag
DraftTurn
```

## User

Represents an authenticated account.

Fields:

```text
Id
DisplayName
Email
PasswordHash
CreatedAt
```

Notes:

```text
User is for login/auth.
SessionPlayer is the player profile inside one match session.
```

A user may join multiple sessions.

## MatchSession

Represents one volleyball draft session.

Fields:

```text
Id
Name
AdminUserId
Status
TeamCount
TeamSize
TotalSets
CurrentRoundNumber
CurrentTurnTeamId
CurrentTurnCaptainUserId
CreatedAt
UpdatedAt
```

Status enum:

```text
Setup
CaptainSelection
Drafting
Finished
Cancelled
```

Rules:

```text
Only AdminUserId can manage setup.
Only current captain can open blind bag.
```

## SessionPlayer

Represents a player inside a specific match session.

Fields:

```text
Id
SessionId
UserId nullable
DisplayName
Role
Level
Score
IsPresent
IsCaptainEligible
IsInsideSharedSlot
CreatedAt
```

Notes:

```text
UserId can be nullable for MVP if admin manually enters players who do not have accounts yet.
If UserId is null, that player cannot login as captain.
For login-based captain flow, captains must have UserId.
```

Role enum:

```text
Attack
Defense
Setter
FullStack
New
```

Level enum:

```text
Good
Average
New
```

Default score:

```text
Good = 3
Average = 2
New = 1
FullStack bonus = 0.5
```

## Team

Represents a team in one session.

Fields:

```text
Id
SessionId
Name
CaptainSessionPlayerId
CaptainUserId nullable
TotalAverageScore
CreatedAt
```

Example:

```text
Team A
Team B
Team C
```

Rules:

```text
A team must have exactly one captain before draft starts.
Captain belongs to the team as the first slot/member.
```

## DraftSlot

Represents one draftable unit.

This is important.

Do not draft raw players directly.

Draft slots can be:

```text
Single
Shared
```

Fields:

```text
Id
SessionId
Type
DisplayName
Role
AverageScore
AssignedTeamId nullable
IsCaptainSlot
CreatedAt
```

Examples:

```text
Single slot: Nick
Shared slot: Bảo / Bình
Captain slot: Nick assigned to Team A from the beginning
```

Rules:

```text
A single slot contains one player.
A shared slot contains multiple players.
A shared slot counts as one team slot.
A captain slot is assigned to a team before draft starts.
```

## DraftSlotPlayer

Join table between DraftSlot and SessionPlayer.

Fields:

```text
Id
DraftSlotId
SessionPlayerId
RotationOrder
```

Examples:

```text
Slot "Bảo / Bình":
- Bảo, RotationOrder 1
- Bình, RotationOrder 2
```

For single slot:

```text
Slot "Nick":
- Nick, RotationOrder 1
```

## DraftRound

Represents a draft round.

For 3 teams and 18 players:

```text
3 captains are assigned first.
15 remaining slots are drafted.
Each team needs 5 more slots.
There are 5 rounds.
Each round has 3 bags.
```

Fields:

```text
Id
SessionId
RoundNumber
Label
Status
CreatedAt
```

Status enum:

```text
Waiting
Active
Completed
```

Examples:

```text
Round 1 - Cầu tốt
Round 2 - Cầu trung bình
Round 3 - Cầu mới
Round 4 - Mixed
Round 5 - Mixed
```

## BlindBag

Represents one hidden bag in a draft round.

Fields:

```text
Id
SessionId
RoundId
DraftSlotId
BagNumber
IsOpened
OpenedByUserId nullable
OpenedForTeamId nullable
OpenedAt nullable
RowVersion optional
```

Rules:

```text
Frontend must not see DraftSlotId for unopened bags.
A blind bag can be opened once.
A blind bag must belong to the current active round to be opened.
```

## DraftTurn

Represents whose turn it is to open a bag.

Fields:

```text
Id
SessionId
RoundId
TeamId
CaptainUserId
TurnOrder
Status
OpenedBagId nullable
CreatedAt
CompletedAt nullable
RowVersion optional
```

Status enum:

```text
Waiting
Active
Completed
Skipped
```

Rules:

```text
There must be exactly one Active DraftTurn during drafting.
Only CaptainUserId of the Active DraftTurn can open a bag.
After opening, mark Active turn Completed and activate the next Waiting turn.
```

## Relationships

```text
User 1 - many MatchSessions as Admin
User 1 - many SessionPlayers
MatchSession 1 - many SessionPlayers
MatchSession 1 - many Teams
MatchSession 1 - many DraftSlots
DraftSlot 1 - many DraftSlotPlayers
DraftRound 1 - many BlindBags
DraftRound 1 - many DraftTurns
Team 1 - many DraftSlots through DraftSlot.AssignedTeamId
```

## Captain constraints

Before starting draft:

```text
Session must have 3 teams.
Each team must have a captain.
Captain must be present.
Captain must be eligible.
Captain must not be inside shared slot.
Captain must have UserId if using login-based captain flow.
```

If a player is inside a shared slot:

```text
IsInsideSharedSlot = true
IsCaptainEligible = false
```

## Starting draft

When admin clicks Start Draft, backend must:

```text
1. Validate session is ready.
2. Validate teams and captains.
3. Convert all non-captain players/shared slots into DraftSlots.
4. Exclude captain slots from blind bag pool.
5. Create 5 DraftRounds.
6. Create 3 BlindBags per round.
7. Create 15 DraftTurns:
   - Round 1 Team A captain
   - Round 1 Team B captain
   - Round 1 Team C captain
   - Round 2 Team A captain
   - ...
8. Set first DraftTurn to Active.
9. Set session.Status = Drafting.
10. Set CurrentRoundNumber, CurrentTurnTeamId, CurrentTurnCaptainUserId.
```

## Draft round generation

Simple MVP algorithm:

```text
1. Get remaining unassigned DraftSlots.
2. Sort by AverageScore descending.
3. Chunk into groups of 3.
4. Each chunk becomes one round.
5. Shuffle DraftSlot order inside each round.
6. Create BlindBags for that round.
```

Example:

```text
Round 1: 3.5, 3, 3
Round 2: 2.5, 2, 2
Round 3: 2, 2, 1.5
Round 4: 1, 1, 1
Round 5: mixed
```

## Important security rule

Unopened bag response must never expose:

```text
DraftSlotId
DisplayName
PlayerIds
AverageScore
Role
```

For unopened bags, return only:

```json
{
  "id": "bag-1",
  "label": "Túi 1",
  "isOpened": false
}
```

For opened bags, it is okay to return:

```json
{
  "id": "bag-1",
  "label": "Túi 1",
  "isOpened": true,
  "revealedSlot": {
    "displayName": "An",
    "role": "Attack",
    "averageScore": 2
  }
}
```

## Balance calculation

Average balance:

```text
Sum AverageScore of all DraftSlots assigned to each team.
Compare max team score and min team score.
```

Status:

```text
Difference <= 1: Balanced
Difference > 1 and <= 2: SlightlyUnbalanced
Difference > 2: StronglyUnbalanced
```

Set balance:

```text
For each set:
- Single slot uses its only player score.
- Shared slot uses player according to rotation order.
- Sum actual player scores per team.
```

Example shared slot:

```text
Bảo / Bình
Set 1: Bảo score 1
Set 2: Bình score 3
Set 3: Bảo score 1
Set 4: Bình score 3
```

## Migration notes

Use EF Core migrations.

SQLite is enough for MVP.

Later, PostgreSQL can replace SQLite without changing the domain model too much.
