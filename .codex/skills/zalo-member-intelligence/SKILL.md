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
- Never assume activity coverage begins when the listener was first started.
- Never require an administrator to manually import every poll for analytics.
- Use `ZaloUserId` as member identity; never identify or mutate a member by display name alone.
- Treat learned application knowledge as approved application data, not model fine-tuning.
- Give every routing or analytics defect a focused regression test.

For message history, expose an honest capability state. If full backfill cannot be proven, store the limitation and prevent analytics from presenting missing history as inactivity.

For sensitive group-wide or other-member analytics, authorize by stable UID using existing application admin, group creator/admin/deputy, or configured operator rules. Allow ordinary members to inspect only their own activity.

Keep deterministic factual output available when AI is unavailable. If optional AI rewriting changes or omits protected names, dates, counts, percentages, or coverage warnings, discard it and return the deterministic answer.
