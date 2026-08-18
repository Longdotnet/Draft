# Zalo Auto Session V2 — Organizer Preview First

## Goal

The production goal is deliberately narrow:

```text
current Zalo group creator/admin creates a poll
        ↓
Auto Session understands whether it is the weekly volleyball signup poll
        ↓
parse schedule options + apply only approved learned defaults
        ↓
mention the exact poll creator with a website preview inside Zalo
        ↓
creator/admin replies to that exact preview message
        ↓
existing deterministic confirmation + transaction creates MatchSession(s)
        ↓
website roster continues syncing from Zalo poll votes
```

There is no automatic waitlist promotion in this flow. If somebody removes their vote, the Zalo slot is simply free and the next person who votes gets it.

## Organizer preview

Live mode always sends a preview before website creation. The preview includes:

- parsed weekday/date/time for every candidate option;
- current poll vote snapshot;
- 3 teams × configured team size and effective capacity;
- configured set count and location;
- explicit instructions for creating all or only selected website sessions.

Supported examples already pass through the deterministic confirmation parser:

- `xác nhận tạo website`
- `tạo T6 CN`
- `T4 18h rồi tạo`
- `bỏ qua`

The reply must quote the exact provider preview message and the sender must still be the current group creator/admin. AI never authorizes the write.

## Phase 1 — Canary rollout

Every tracked group has an independent rollout policy:

- `Disabled`: Auto Session V2 ignores new poll discovery for the group.
- `PreviewOnly`: classifier/parser runs and the exact poll creator receives the preview, but the proposal is not left in `AwaitingApproval`, so replying cannot create a website session.
- `Live`: preview is sent first and only an authorized reply to that exact message can create website sessions.

The default for existing groups is `Live` to preserve pre-V2 behavior. For a canary rollout, set all non-test groups to `PreviewOnly` or `Disabled` and keep only the chosen group in `Live`.

## Phase 2 — Production hardening

`ZaloAutoSessionRuntimeSettings` contains a global kill switch. When `GlobalEnabled=false`:

- real-time board events do not generate previews;
- periodic discovery does not process polls;
- pending proposal confirmations are not executed by the V2 worker.

Per-group health records expose:

- last board event;
- last reconciliation;
- last successful run;
- last error;
- consecutive failure count;
- next retry time.

Transient group failures use exponential backoff starting at 30 seconds and capped at 15 minutes. A successful pass clears the failure count and retry deadline.

## Phase 4 — Organizer UX

The preview is addressed to the organizer who created the poll rather than tagging every admin. The bot states clearly that no website has been created yet and gives deterministic reply examples.

`PreviewOnly` uses a separate idempotency key and explicitly says that replies will not create sessions. Switching the same group to `Live` allows the same poll structure to receive a new live proposal without waiting for the poll to change.

## Phase 9 — Controlled learning

Learning is feedback-driven, not autonomous.

After a confirmed proposal becomes `Created`, reconciliation compares the stored preview candidates against the actual created MatchSession start times and option links.

Signals include:

- `default_day_time_correction`: organizer changed a time for an option that had no explicit time in the poll. This can suggest `default_day_time` for that weekday.
- `one_off_time_override`: organizer changed an option that already contained an explicit time. Stored as feedback only; it is not promotable into a default rule.
- `selection_override`: organizer intentionally created only a subset of previewed options. Stored as feedback only.
- `classification_rejection`: organizer rejected an accepted poll proposal. Stored for classifier evaluation only.

All new signals start as `Pending`. Only an authenticated admin can mark them `Approved` or `Rejected`. Only approved `default_day_time` signals affect future previews, and only when the future poll option does not already contain an explicit time.

## Operations UI

The desktop `Auto Session Operations` panel uses the existing authenticated `/api/zalo/auto-session-groups` GET/PUT routes and adds no new endpoint surface.

It provides:

- global kill switch;
- per-group Disabled / PreviewOnly / Live rollout;
- connection/reconciliation/backoff health;
- pending learning review and explicit Approve/Reject actions.

The normal Auto Session settings panel continues to own team size, sets, default time, location and bot settings. Audit remains read-only.
