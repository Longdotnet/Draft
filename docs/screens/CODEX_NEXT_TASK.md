# CODEX_NEXT_TASK.md

## Task

Continue implementing Volley Draft MVP with database-backed captain permission flow.

Focus only on:

```text
DB schema
Backend permission logic
Captain selection
Blind bag draft state
Open bag authorization
Basic frontend integration
```

Do not overbuild unrelated features.

## Current product decision

The app now supports a login/DB-backed flow.

There are 18 present players.

The app creates 3 teams.

There are 2 captain selection modes:

```text
1. Auto-select 3 balanced captains.
2. Admin manually selects or edits 3 captains.
```

After captains are confirmed:

```text
Each captain belongs to one team.
Each team starts with its captain.
The remaining players/slots are placed into blind bags.
Captains take turns opening blind bags.
Only the current active captain can open one bag.
```

## Main implementation priority

Implement this backend rule first:

```text
Only ActiveTurn.CaptainUserId can open a blind bag.
```

Frontend button state is not enough.

Backend must enforce it.

## Build order

### Step 1: Add database entities

Add these entities:

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

Add enums:

```text
SessionStatus:
- Setup
- CaptainSelection
- Drafting
- Finished
- Cancelled

DraftTurnStatus:
- Waiting
- Active
- Completed
- Skipped

DraftRoundStatus:
- Waiting
- Active
- Completed

DraftSlotType:
- Single
- Shared

PlayerRole:
- Attack
- Defense
- Setter
- FullStack
- New

PlayerLevel:
- Good
- Average
- New
```

### Step 2: Add JWT auth

Implement:

```http
POST /api/auth/register
POST /api/auth/login
GET /api/auth/me
```

For MVP, keep auth simple.

Do not add OAuth.

### Step 3: Add session setup APIs

Implement:

```http
POST /api/sessions
GET /api/sessions/{id}
POST /api/sessions/{id}/players
GET /api/sessions/{id}/players
```

Only session admin can add/edit players.

### Step 4: Add captain APIs

Implement:

```http
POST /api/sessions/{id}/captains/auto-select
PUT /api/sessions/{id}/captains/manual
GET /api/sessions/{id}/captains
```

Auto-select rule:

```text
Pick 3 present eligible players with closest scores.
Prefer score difference <= 0.5.
If impossible, allow <= 1.
If still impossible, return closest group with warning.
```

Manual captain rule:

```text
Only admin can manually set captains.
No duplicate captains.
Captains must be present.
Captains must be eligible.
Captains must not be inside shared slots.
Captains should have UserId for login-based flow.
```

After captains are set:

```text
Create or update Team A, Team B, Team C.
Assign captain slot to each team.
Set session status to CaptainSelection or keep ready-to-start state.
```

### Step 5: Add start draft API

Implement:

```http
POST /api/sessions/{id}/start-draft
```

Only admin can call this.

Backend must validate:

```text
Session has 3 teams.
Each team has one captain.
There are enough remaining draftable slots.
Captains are valid.
```

Then backend must:

```text
Create DraftSlots for non-captain players and shared slots.
Create DraftRounds.
Create BlindBags.
Create DraftTurns.
Set first DraftTurn to Active.
Set session status to Drafting.
Set current round/team/captain on MatchSession.
```

### Step 6: Add draft-state API

Implement:

```http
GET /api/sessions/{id}/draft-state
```

Backend returns:

```text
Session status
Current round
Current team
Current captain
Viewer role
Viewer canOpenBag
Bags for current round
Opened result if any
Team preview
```

Important:

```text
Do not reveal unopened bag contents.
```

If viewer is not current captain:

```json
{
  "canOpenBag": false,
  "message": "Đang chờ Nick khui túi cho Team A."
}
```

If viewer is current captain:

```json
{
  "canOpenBag": true,
  "message": "Tới lượt bạn khui túi cho Team A."
}
```

### Step 7: Add open bag API

Implement:

```http
POST /api/sessions/{id}/blind-bags/{bagId}/open
```

Use authenticated user from JWT.

Use DB transaction.

Backend checks:

```text
1. User is authenticated.
2. Session exists.
3. Session status is Drafting.
4. Active DraftTurn exists.
5. Active DraftTurn.CaptainUserId == current user ID.
6. Bag belongs to active round.
7. Bag is not opened.
8. DraftSlot inside bag is not already assigned.
```

If valid:

```text
Open bag.
Set OpenedByUserId.
Set OpenedForTeamId.
Assign DraftSlot.AssignedTeamId.
Complete active DraftTurn.
Activate next waiting DraftTurn.
If no next turn, set session status to Finished.
Save changes.
Return revealed slot and next turn.
```

If invalid current user:

```text
Return 403 Forbidden.
```

If bag already opened:

```text
Return 400 Bad Request or 409 Conflict.
```

### Step 8: Frontend integration

Frontend should call:

```http
GET /api/sessions/{id}/draft-state
```

every 2 seconds while drafting.

If `viewer.canOpenBag` is true:

```text
Enable blind bag cards.
```

If false:

```text
Disable blind bag cards.
Show waiting message.
```

When clicking a bag:

```text
POST /api/sessions/{id}/blind-bags/{bagId}/open
```

After success:

```text
Show reveal message.
Refresh draft-state.
```

If 403:

```text
Show: "Chưa tới lượt bạn khui túi."
```

If 409:

```text
Show: "Túi đã được mở hoặc lượt bốc đã thay đổi. Vui lòng tải lại."
```

## Do not implement yet

Do not implement these in this task:

```text
SignalR realtime
Complex room invitations
Payment
Public leaderboard
Advanced analytics
Mobile app
OAuth login
Admin dashboard beyond session setup
```

Use polling for MVP.

## Acceptance criteria

This task is done when:

```text
1. Admin can create a session and players.
2. Admin can auto-select 3 balanced captains.
3. Admin can manually override captains.
4. Backend creates teams with captains.
5. Backend starts the draft and creates rounds, bags, and turns.
6. Draft-state endpoint returns current turn and canOpenBag.
7. Only the current active captain can open a bag.
8. Non-current users receive 403 when trying to open a bag.
9. Opened bag assigns slot/player to the correct team.
10. After opening, the next turn becomes active.
11. Unopened bag contents are not leaked to frontend.
12. Double-click/race condition is protected with transaction.
```

## Important wording for frontend

Use Vietnamese wording:

```text
Lượt hiện tại
Đại diện
Đội trưởng
Khui túi
Bốc được
Đang chờ
Chưa tới lượt bạn
Túi đã được mở
```

Preferred reveal message:

```text
Nick đã khui túi và bốc được An cho Team A.
```

Avoid:

```text
Nick đã chọn An.
```

because captain does not manually choose the player.
