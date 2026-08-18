# Zalo Poll -> Auto Match Session

This feature removes the manual `create session -> link group -> choose poll -> choose option` setup for the normal weekly volleyball signup flow.

## Runtime flow

```text
Zalo group board event / periodic reconciliation
        |
        v
tracked group
        |
        v
poll schedule parser (T2-T7/CN + explicit time)
        |
        v
rule classifier + configured AI classifier
        |
        v
creator/admin authorization check
        |
        v
proposal message mentioning current group creator/admins
        |
        v
exact reply to provider proposal message
        |
        +-- bo qua / khong tao -> reject
        |
        +-- tao ca N / xac nhan -> all parsed sessions
        |
        +-- chi T6 CN -> selected sessions
        |
        +-- T4 doi 18h -> selected session + time override
        v
one MatchSession per poll option
        |
        +-- PollImport binds the exact option id
        +-- current voters are synced immediately
        +-- future board events reuse SyncLatestPollAsync
        +-- existing overbook state is enabled automatically
```

## Safety boundary

AI never authorizes a write. The write path requires all of these:

1. the Zalo group is tracked;
2. the poll is non-anonymous and open;
3. the poll contains at least one parseable weekday option;
4. the poll creator is the current Zalo group creator or admin;
5. the rule/AI classifier accepts the poll as a volleyball signup poll;
6. when organizer approval is enabled, the reply quotes the exact provider message id returned for the proposal;
7. the confirming sender is still the current group creator/admin;
8. the poll structure hash still matches;
9. `TrackedGroupId + PollId + OptionId` has not already claimed a session.

The structure hash intentionally excludes voter ids/counts. A normal 18 -> 19 vote update must not invalidate a pending organizer proposal. It changes only when the poll question/options or relevant poll structure changes.

## Tracking bootstrap and admin UI

The first rollout seeds `ZaloTrackedGroups` from groups that have already been linked to at least one `MatchSession`. Once seeded, the listener keeps the group subscribed independently of active sessions, which removes the old circular dependency:

```text
need session -> to listen group -> to discover poll -> to create session
```

The desktop admin UI also allows selecting any group from an owned connected Zalo account and enabling Auto Session before that group has ever had a MatchSession. The backend validates that the group is still visible to that Zalo connection and reconciles the listener immediately after enable/disable changes.

## Per-group settings

The admin panel exposes these persisted settings:

- enable/disable Auto Session;
- require current group creator/admin approval before session creation;
- player count per team (team count remains fixed at 3 for the MVP);
- total sets;
- default start time used when a poll option has a weekday but no explicit time;
- whether hours `1..11` should be interpreted as PM for weekly volleyball polls;
- default location;
- whether auto-created sessions start with the Zalo bot enabled.

Disabling organizer approval is explicit in the UI because it changes the authorization boundary: an accepted organizer-created poll can then create sessions without a second confirmation message.

## Default schedule behavior

- timezone: Vietnam (`UTC+07:00`)
- T2-T7 and CN are supported
- explicit `17:30`, `17h30`, `5h30` are supported
- for volleyball signup, hours `1..11` are interpreted as PM by default (`5h30 -> 17:30`)
- missing time falls back to 17:30
- the weekday resolves to the next occurrence relative to poll creation time

## Default match settings

- 3 teams
- 6 players/team
- capacity 18
- 4 sets
- bot enabled
- group creator/admin ids become bot operators for auto-created sessions
- existing poll sync remains the roster source of truth
- existing overbook logic handles 19th+ effective slots and does not delete Zalo votes automatically

## Configuration

All settings have safe defaults and are optional:

```json
{
  "AutoSession": {
    "Enabled": true,
    "ReconcilePollLimit": 20,
    "PollMaxAgeDays": 21,
    "ConfirmationHistoryCount": 200,
    "OverbookGraceMinutes": 5,
    "OverbookReminderMinutes": 30,
    "OverbookMaxReminders": 5
  }
}
```

The periodic reconciliation path is deliberate. The real-time poll queue is bounded and in-memory, so deploys/restarts or dropped board events must not permanently lose a weekly poll.

## Idempotency

`ZaloAutoSessionLinks` has a unique key on:

```text
TrackedGroupId + PollId + OptionId
```

The link is claimed before the session is inserted inside the same database transaction. Multiple event deliveries or multiple reconciliation passes therefore converge on the same session instead of duplicating it.
