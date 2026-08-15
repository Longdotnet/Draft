# Zalo Ambient Social AI Pilot

This phase starts Ambient Phase 3 with a **shadow-first social AI path**. It is intentionally separate from fact answering, business actions and durable memory.

## What counts as Social

The pilot only considers a turn when all of these are true:

1. ambient observation is enabled for the bot-enabled group;
2. the turn is not classified as a Fact or Action candidate;
3. deterministic intent is `Unknown` or `GeneralChat`;
4. the conversational address resolver says the user is talking to the bot with high confidence;
5. the speech act is ordinary social conversation, not capability/preview/mutation/confirmation/cancel;
6. a quoted message, if present, is a reply to the bot rather than another member;
7. the text does not begin as a human vocative such as `Nam ơi, ...`;
8. the effective social score meets `SocialPilot.MinimumScore`.

This is intentionally narrower than general group conversation. Silence is preferred over interrupting a human-to-human exchange.

## Three rollout gates

```json
{
  "ZaloBot": {
    "Ambient": {
      "ShadowMode": true,
      "SocialPilot": {
        "Enabled": false,
        "SendEnabled": false,
        "MinimumScore": 90,
        "MaxContextMessages": 8,
        "MaxReplyChars": 180
      }
    }
  }
}
```

The gates mean:

- `SocialPilot.Enabled=false`: no social AI call at all.
- `SocialPilot.Enabled=true` + `SendEnabled=false`: the system may generate a safe candidate and emit metadata-only shadow trace, but never sends it.
- user-visible send requires **both** `SocialPilot.SendEnabled=true` and global `Ambient.ShadowMode=false`.

The source defaults keep all social outbound behavior disabled.

## Isolation from memory and domain writes

Social AI does **not** call `AiAssistantService.AnswerAsync`. That method can enrich self-concept memory and is therefore inappropriate for unaddressed social banter.

`ZaloAmbientSocialResponder` instead uses a dedicated AI request with only:

- the current social message;
- a bounded recent-message window selected from IDs already produced by the ambient observer;
- a social-only system policy.

It does not load or write user concepts, pending commands, poll state, roster state, team state, waitlist state or reminders.

The AI is instructed to return `__NO_REPLY__` whenever the turn needs business facts or an operation.

## Output safety filter

Even after generation, the candidate is dropped if it:

- exposes internal reasoning/system-prompt language;
- claims an operation was completed (`đã thêm`, `đã cập nhật`, `đã draft`, ...);
- claims durable memory was written (`đã ghi nhớ`, `lưu rồi`, ...);
- contains `@all`;
- contains an HTTP/HTTPS URL;
- exceeds the configured reply length.

A failed or unsafe candidate becomes silence, never a fallback business answer.

## Send/idempotency path

When the live gates are intentionally enabled, the send path uses:

```text
ambient-social:{accountId}:{incomingMessageId}
```

as the stable bridge idempotency key. It atomically claims the observed incoming message before sending.

After a provider-successful send:

- provider message ID is persisted through the existing V2 outbound/message-graph path;
- the incoming message is marked `ambient_social_sent` with `AiCalled=true`;
- a terminal `AmbientSocialPilot` trace is written;
- persistence/trace failures after provider success never trigger a second user-visible send from the same invocation.

## Truth hierarchy remains unchanged

Social chat can never become registration truth. The authority order remains:

1. authorization/security;
2. current DB and Zalo poll state;
3. current message;
4. scoped context/memory where explicitly applicable;
5. AI inference.

Examples such as `tui chắc đi T6`, `cho tui một slot nha`, or jokes about joining a team must not register, withdraw or move anyone. Registration remains poll/domain state and operational changes remain behind their existing explicit authorization/confirmation flows.
