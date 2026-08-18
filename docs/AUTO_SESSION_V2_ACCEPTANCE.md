# Auto Session V2 acceptance checklist

## Canary

- Set global switch ON.
- Set exactly one test group to `Live`.
- Set other tracked groups to `PreviewOnly` or `Disabled`.
- Organizer creates a weekly volleyball poll.
- Bot mentions exactly the poll creator and sends PREVIEW WEBSITE.
- In PreviewOnly, replying does not create a MatchSession.
- In Live, `xác nhận tạo website` creates the previewed sessions once.
- Duplicate board events do not duplicate the proposal/session.

## Context understanding

Test at least:

- `T4`, `T6`, `CN` with no explicit time;
- `T4 5h30`, `T6 17:30`, `CN 16h`;
- non-volleyball weekday poll;
- anonymous poll;
- poll created by a normal member.

Only a volleyball signup poll created by current group creator/admin should receive an actionable preview.

## Organizer commands

- `xác nhận tạo website` → all previewed options.
- `tạo T6 CN` → selected days only.
- `T4 18h rồi tạo` → T4 only with 18:00 override.
- `bỏ qua` → no website session.

All commands must quote the exact preview message.

## Production hardening

- Global OFF prevents new previews and confirmation execution.
- Connection/health fields update after event/reconcile.
- Bridge failures increase consecutive failure count and set NextRetryAt.
- Successful reconciliation resets failure/backoff state.

## No automatic waitlist

- Fill a session to capacity.
- One voter leaves the Zalo poll.
- No waitlist promotion runs automatically.
- The first person who votes into the newly free Zalo slot owns it through normal poll sync.

## Controlled learning

- Use a poll option with only a weekday and no explicit time.
- Preview uses configured/default time.
- Organizer changes that day time in confirmation.
- A Pending `default_day_time_correction` signal appears.
- Before approval, another poll still uses the old default.
- After admin Approve, a later option for that weekday without explicit time uses the approved learned time.
- An option that already contains an explicit time always keeps its explicit time.
