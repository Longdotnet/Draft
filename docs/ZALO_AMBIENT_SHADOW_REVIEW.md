# Zalo Ambient Shadow Review Runbook

Use this runbook after deploying the ambient observer with `ZaloBot:Ambient:ShadowMode=true`.

The goal is to answer one question before Phase 2: **when the bot says it would participate, is that actually useful often enough to let it speak in the group?**

## Safety baseline

Keep these settings during observation:

```json
{
  "ZaloBot": {
    "Ambient": {
      "Enabled": true,
      "ShadowMode": true,
      "WouldReplyThreshold": 65
    }
  }
}
```

There is no outbound ambient send path in Phase 1. Explicit `@bot` and verified reply-to-bot flows remain unchanged.

## Aggregate report

`ZaloAmbientShadowMetricsService` aggregates one owned session/group without reading raw chat text. It reports:

- observed ambient turns;
- `WouldReply` count/rate;
- average participation score;
- high-confidence Fact candidates;
- Fact/Social/Action/None candidate counts;
- top intents;
- suppression reason counts.

For direct production inspection, the equivalent PostgreSQL query is:

```sql
SELECT
    COUNT(*) AS observed_count,
    COUNT(*) FILTER (WHERE "AddressReason" = 'AmbientShadowWouldReply') AS would_reply_count,
    ROUND(AVG(COALESCE("Confidence", 0)) * 100, 2) AS average_score,
    COUNT(*) FILTER (
        WHERE "AddressReason" = 'AmbientShadowWouldReply'
          AND COALESCE("FallbackReason", '') LIKE 'kind:Fact%'
    ) AS high_confidence_fact_count
FROM "ZaloBotTraces"
WHERE "IntentSource" = 'AmbientShadow'
  AND "GroupId" = :group_id
  AND "CreatedAt" >= NOW() - INTERVAL '24 hours';
```

## Candidate-kind distribution

The shadow trace stores the candidate kind as the first compact metadata token, for example:

```text
kind:Fact|fact_intent|question|session_reference|quiet_group
```

PostgreSQL:

```sql
SELECT
    split_part(COALESCE("FallbackReason", 'kind:Unknown'), '|', 1) AS candidate_kind,
    COUNT(*) AS total
FROM "ZaloBotTraces"
WHERE "IntentSource" = 'AmbientShadow'
  AND "GroupId" = :group_id
  AND "CreatedAt" >= NOW() - INTERVAL '24 hours'
GROUP BY 1
ORDER BY total DESC;
```

## Review the messages the bot would have answered

This query intentionally joins the existing retained group message only for manual rollout review. The shadow trace itself does not duplicate raw message content.

```sql
SELECT
    t."CreatedAt",
    t."MessageId",
    m."SenderName",
    m."Content",
    t."Intent",
    ROUND(COALESCE(t."Confidence", 0) * 100, 0) AS score,
    t."FallbackReason"
FROM "ZaloBotTraces" t
JOIN "ZaloGroupMessages" m
  ON m."GroupId" = t."GroupId"
 AND m."MessageId" = t."MessageId"
WHERE t."IntentSource" = 'AmbientShadow'
  AND t."AddressReason" = 'AmbientShadowWouldReply'
  AND t."GroupId" = :group_id
  AND t."CreatedAt" >= NOW() - INTERVAL '24 hours'
ORDER BY t."CreatedAt" DESC
LIMIT 100;
```

For each sampled row, label it manually as one of:

- `useful_fact_reply` — the bot should have answered;
- `human_thread_intrusion` — people were already talking to each other;
- `not_a_real_question` — rhetorical/joke/no answer needed;
- `wrong_intent` — deterministic routing picked the wrong domain intent;
- `needs_more_context` — answering from one turn would be unsafe/ambiguous.

## Go/no-go gate for Fact Participant

Do **not** enable outbound ambient replies based only on aggregate score. A high score is not the same as production precision.

Recommended minimum evidence before a small Fact-only pilot:

1. At least 200 unaddressed messages have been observed in the target group.
2. Manually review at least 30 `AmbientShadowWouldReply` candidates, or all candidates if fewer exist.
3. At least 90% of the reviewed candidates are `useful_fact_reply`.
4. No reviewed Action/mutation request would have been executed through ambient mode.
5. Reply-to-member and bot-cooldown cases remain hard-suppressed.
6. Duplicate webhook delivery still produces one ambient trace and no duplicate business action.

If precision is below the gate, tune deterministic intent rules/thresholds first. Do not compensate by asking an AI model to decide authorization or business truth.

## Phase 2 pilot scope

The first outbound pilot should be restricted to a small allowlist of read-only Fact intents, for example:

- schedule/time;
- court/location/parking;
- remaining slots;
- roster/list membership;
- upcoming sessions.

Do not include draft/redraft, waitlist mutations, profile changes, guest creation, transfers, reminder changes, or any other write operation in the ambient path.
