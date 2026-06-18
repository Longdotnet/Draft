# AUTH_DRAFT_PERMISSION_SKILL.md

## Goal

Implement the blind bag draft permission system for Volley Draft MVP.

The main rule:

```text
Only the captain of the current active turn can open a blind bag.
```

Frontend must not decide permission by itself. Backend must enforce permission.

## Product flow

The app flow is:

```text
1. Admin creates a match session.
2. Admin adds 18 present players.
3. The system creates 3 teams.
4. The system auto-selects 3 balanced captains OR admin manually selects captains.
5. Each captain belongs to one team.
6. Captains start as the first member of their team.
7. The remaining players/slots are put into blind bags.
8. Captains take turns opening blind bags.
9. Only the current team's captain can open a bag.
10. After a captain opens one bag, the system assigns that slot/player to the captain's team.
11. The system moves to the next captain turn.
12. When all rounds are complete, the draft is finished.
```

## Key permission rule

Backend must check all of these before allowing a bag to be opened:

```text
1. Current user is authenticated.
2. Session exists.
3. Session status is Drafting.
4. There is exactly one active DraftTurn.
5. Current user ID equals ActiveTurn.CaptainUserId.
6. Selected bag belongs to the current active round.
7. Selected bag is not opened.
8. Active turn has not been completed.
9. Draft slot inside the bag is not assigned to another team.
```

If any rule fails, return an error.

Recommended errors:

```text
401 Unauthorized: user is not logged in
403 Forbidden: user is not the current captain
400 Bad Request: bag already opened / wrong round / session not drafting
404 Not Found: session or bag does not exist
```

## Roles

For MVP, use these session-level roles:

```text
Admin
Captain
Player
Viewer
```

### Admin can:

```text
Create session
Add players
Edit players
Create shared slots
Auto-select captains
Manually override captains
Start draft
Reset draft
View results
```

### Captain can:

```text
View session
View draft state
Open one blind bag only when it is their active turn
View team result
```

### Player / Viewer can:

```text
View draft state
View team result
Cannot open blind bags
Cannot manage captains
Cannot edit session
```

## Important rule about admin

Admin can manage setup, but admin should not be allowed to open blind bags unless the admin is also the current active captain.

Do not allow this:

```text
Admin opens bags for every team during captain mode.
```

Allow this only:

```text
Admin opens a bag if AdminUserId == ActiveTurn.CaptainUserId.
```

## Captain selection

There are two modes:

```text
Auto balanced captains
Manual captain selection by admin
```

### Auto balanced captains

The backend selects 3 captains from present eligible players.

Rules:

```text
Player must be present.
Player must be captain eligible.
Player must not be inside a shared slot.
Player must be a single player slot.
```

Balance rule:

```text
Prefer 3 captains whose score difference is <= 0.5.
If impossible, allow <= 1.
If still impossible, select the closest possible group and return a warning.
```

Example good captain group:

```text
Nick: 3
Bình: 3
Minh: 3
```

Example acceptable captain group:

```text
Sin: 3.5
Cường: 3.5
Nick: 3
```

Example bad captain group:

```text
Sin: 3.5
Bảo: 1
Huy: 1
```

Return balance info after selecting captains:

```json
{
  "difference": 0.5,
  "status": "Balanced",
  "warning": null
}
```

or:

```json
{
  "difference": 2.5,
  "status": "StronglyUnbalanced",
  "warning": "3 đại diện đang lệch trình. Admin nên chọn lại."
}
```

### Manual captain selection

Admin can manually set captains.

Backend must validate:

```text
Current user is session admin.
All selected captain IDs belong to this session.
No duplicate captain.
Captains are present.
Captains are captain eligible.
Captains are not inside shared slots.
```

Admin can still confirm slightly unbalanced captains, but backend should return a warning.

## Draft state

Frontend should call:

```http
GET /api/sessions/{sessionId}/draft-state
```

Backend returns current state.

Example response for current captain:

```json
{
  "sessionStatus": "Drafting",
  "currentRound": 1,
  "totalRounds": 5,
  "currentTeam": {
    "id": "team-a",
    "name": "Team A"
  },
  "currentCaptain": {
    "id": "user-nick",
    "name": "Nick"
  },
  "viewer": {
    "id": "user-nick",
    "canOpenBag": true,
    "role": "Captain"
  },
  "bags": [
    { "id": "bag-1", "label": "Túi 1", "isOpened": false },
    { "id": "bag-2", "label": "Túi 2", "isOpened": false },
    { "id": "bag-3", "label": "Túi 3", "isOpened": false }
  ]
}
```

Example response for non-current user:

```json
{
  "sessionStatus": "Drafting",
  "currentRound": 1,
  "totalRounds": 5,
  "currentTeam": {
    "id": "team-a",
    "name": "Team A"
  },
  "currentCaptain": {
    "id": "user-nick",
    "name": "Nick"
  },
  "viewer": {
    "id": "user-binh",
    "canOpenBag": false,
    "role": "Captain"
  },
  "message": "Đang chờ Nick khui túi cho Team A."
}
```

Important:

```text
Do not expose hidden DraftSlot identity inside unopened bags.
```

Frontend can show:

```text
Túi 1
Túi 2
Túi 3
```

But must not know:

```text
Túi 1 contains An
Túi 2 contains Long
Túi 3 contains Huy
```

until the bag is opened.

## Open blind bag endpoint

Endpoint:

```http
POST /api/sessions/{sessionId}/blind-bags/{bagId}/open
```

Request body can be empty.

Backend uses authenticated user ID from JWT.

Backend must:

```text
1. Begin database transaction.
2. Load session.
3. Load active DraftTurn.
4. Check current user is active captain.
5. Load selected BlindBag.
6. Check bag belongs to active round.
7. Check bag is not opened.
8. Check bag's DraftSlot is not assigned.
9. Mark bag as opened.
10. Set OpenedByUserId.
11. Set OpenedForTeamId.
12. Assign DraftSlot to active team.
13. Mark active DraftTurn as Completed.
14. Activate next DraftTurn.
15. If no next turn, set session status to Finished.
16. Commit transaction.
17. Return reveal result.
```

Example response:

```json
{
  "message": "Nick đã khui túi và bốc được An cho Team A.",
  "revealedSlot": {
    "id": "slot-an",
    "displayName": "An",
    "type": "Single",
    "averageScore": 2
  },
  "assignedTeam": {
    "id": "team-a",
    "name": "Team A"
  },
  "nextTurn": {
    "teamName": "Team B",
    "captainName": "Bình",
    "roundNumber": 1
  }
}
```

## Race condition protection

The open bag action must be protected from:

```text
Double click
Two users opening at the same time
Same captain opening two bags
Opening an already opened bag
Opening a bag from another round
```

Use database transaction.

Recommended:

```text
Use EF Core transaction.
Add RowVersion concurrency token to BlindBag and DraftTurn if possible.
```

If concurrency conflict occurs, return:

```text
409 Conflict
```

Message:

```text
Túi này đã được mở hoặc lượt bốc đã thay đổi. Vui lòng tải lại trạng thái bốc thăm.
```

## Frontend behavior

Frontend should still show/hide buttons for good UX:

```text
If viewer.canOpenBag == true:
  Show clickable blind bags.

If viewer.canOpenBag == false:
  Disable bags.
  Show message: "Đang chờ Nick khui túi cho Team A."
```

But never rely on frontend only.

Backend must always check permission again.

## Polling

For MVP, do not implement SignalR yet.

Use polling:

```text
Frontend calls GET /draft-state every 2 seconds while session status is Drafting.
```

Later version can replace polling with SignalR.
