---
name: zalo-member-intelligence
description: Implement and review production Zalo member synchronization, historical backfill, poll/message activity analytics, Vietnamese bot intents, privacy, authorization, and regression tests in VolleyDraft. Use for any work involving Zalo member identity, historical boards/polls/messages, engagement metrics, inactive-member reports, or Member Intelligence UI/API behavior.
---

# Zalo Member Intelligence

Follow this order:

1. Inspect the current repository and installed Zalo library before designing changes.
2. Prove each Zalo capability with source inspection and an isolated probe before exposing it in production.
3. Persist resumable, idempotent synchronization checkpoints before adding analytics queries.
4. Calculate factual results in C# and PostgreSQL; use AI only for structured Vietnamese intent extraction and optional wording.
5. Add a regression test for every routing, synchronization, identity, authorization, analytics, or coverage bug.
6. Run backend, frontend, ZaloBridge, and CI-equivalent builds/tests before completion.

Enforce these rules:

- Let AI understand Vietnamese questions and extract typed structured intent.
- Let C# and PostgreSQL calculate every name, date, count, percentage, trend, and activity metric.
- Treat the AI model as neither a database nor an authority over stored facts.
- Never invent messages, polls, voters, members, timestamps, coverage, or exact per-user vote times.
- Automatically retrieve all historical Zalo data that the connected account can actually access.
- Treat durable `ZaloTrackedGroups` configuration as the ownership boundary for background Member Intelligence discovery. A tracked group must remain eligible for initial/incremental activity synchronization even when it currently has no `MatchSession`, all sessions are finished/deleted, or session `BotEnabled` is temporarily false. Keep bot-enabled linked sessions only as a backwards-compatibility discovery fallback for installations that have not seeded tracked groups yet. Do not queue orphan tracked rows whose Zalo connection no longer exists.
- Listener subscription follows durable tracked-group ownership too. `AutoSessionEnabled` controls whether Auto Session acts on poll events; it must not silently unsubscribe an otherwise tracked group and cut off realtime message/poll activity needed by Member Intelligence. Feature processors may decline an event, but transport observation stays attached to the configured group until that group is actually untracked/disconnected.
- Inbound message capture must resolve provider account + group through the same durable target boundary before writing `ZaloGroupMessages`. Persist the canonical connection/group-scoped message ledger even when there is no current bot-enabled `MatchSession`; session-specific bot lanes may then decline the turn without erasing the activity observation. Preserve duplicate-delivery suppression and never allow the same group id on another account to cross this boundary.
- Realtime provider events must use the same durable tracked-group ownership boundary as background discovery. In particular, poll `update_board`/`remove_board` events must still queue incremental Member Intelligence sync for a connected tracked group that currently has no bot-enabled `MatchSession`; never rediscover the activity connection solely from session lifecycle state. Match the inbound provider account + group to the connected durable target so identical group ids on another account cannot cross the boundary.
- Treat realtime poll notifications as dirty-scope signals, not a disposable firehose. Never use a globally lossy `DropOldest` queue for authoritative poll changes: preserve every distinct normalized account/group scope, coalesce repeated notifications for the same scope to the newest event, and schedule a follow-up turn when a new change arrives while that scope is being processed. Do not apply a time-based worker skip that can discard that follow-up; processing fetches authoritative current poll state, so scope coalescing is the safe pressure-control boundary.
- Treat a running activity-backfill lease as authoritative across API instances. Queue/discovery requests may create or atomically requeue a non-running job, but must never clear or overwrite a lease/checkpoint that a worker acquired concurrently. Initial-job creation must tolerate the unique `(ZaloConnectionId, GroupId)` race by reusing the winning row rather than turning normal multi-instance contention into a worker/API failure.
- Never assume activity coverage begins when the listener was first started.
- Never require an administrator to manually import every poll for analytics.
- Use `ZaloUserId` as member identity; never identify or mutate a member by display name alone.
- Do not let a relative-date shortcut silently reinterpret a plausible member name. In particular, Vietnamese `Mai` is both a common name and “tomorrow”; only treat `mai` as a session date when the surrounding language is unambiguously schedule-shaped (for example `trận mai`, `tối mai`, `mai 17 giờ 30`, or `mai đánh mấy giờ`). Questions such as `Mai chơi không?` must remain available to member/person resolution.
- Treat learned application knowledge as approved application data, not model fine-tuning.
- Give every routing or analytics defect a focused regression test.

For message history, expose an honest capability state. If full backfill cannot be proven, store the limitation and prevent analytics from presenting missing history as inactivity.

For sensitive group-wide or other-member analytics, authorize by stable UID using existing application admin, group creator/admin/deputy, or configured operator rules. Allow ordinary members to inspect only their own activity.

Keep deterministic factual output available when AI is unavailable. If optional AI rewriting changes or omits protected names, dates, counts, percentages, or coverage warnings, discard it and return the deterministic answer.
