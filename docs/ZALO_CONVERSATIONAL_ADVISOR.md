# Zalo Conversational Advisor

This phase makes the bot understand natural group-chat turns that talk about or to the bot without requiring an explicit `@bot`, while keeping domain writes behind the existing explicitly-addressed confirmation path.

## Target conversations

### Capability discovery

```text
Long: con bot này sài sao vậy mn
Bot: Tui là bot hỗ trợ kèo của nhóm 😄. ...
```

A clear reference to `bot`/`npc` plus a capability question is treated as a high-confidence read-only conversational turn. The answer comes from `ZaloBotCapabilityRegistry`, not from free-form social AI.

### Team-preference feasibility

```text
Long: tui muốn choi chung với To An thì bạn xếp đc ko
Bot: Được. Tui hiểu là Long muốn chơi chung team với To An. Cả hai đang có mặt ở T6 và CN. Bạn chọn kèo nào?

Long: T6
Bot: ... Cả hai đều đang có trong roster T6. Tui có thể giữ yêu cầu này làm preference ...
```

The advisor:

1. recognizes the turn as addressed to the bot even with chat shorthand such as `đc/ko`;
2. resolves `tui` from the sender Zalo UID;
3. resolves `To An` through `ZaloIdentityResolver` (UID/name/approved alias; ambiguity is never guessed);
4. reads current bot-enabled sessions and poll-synchronized roster state;
5. stores only a short-lived `AmbientTeamPreferenceProposal` in `ZaloConversationStatesV2` when clarification/follow-up is useful;
6. never writes `TeamPreferenceGroup`, draft/team data, registration, slots, waitlist, or other business state.

## Speech-act boundary

The important distinction is not only the domain intent (`TeamPreference`) but what the user is doing with it:

- `AskCapability`: "bot này làm được gì?" -> explain;
- `AskFeasibility`: "tui với To An chung team thì bạn xếp đc ko" -> inspect + advise;
- `RequestPreview`: a conversational same-team request -> keep/read proposal context;
- `RequestMutation`: "xếp tui với To An chung team đi" -> ambient path does not authorize a write;
- `Confirm`/`Cancel` during a proposal -> may continue/cancel proposal state, but ambient confirmation alone does not execute the domain mutation.

A real mutation continues to require the normal explicitly-addressed/authorized handler and its confirmation contract.

## Human-thread protection

Messages such as:

```text
Nam ơi bạn chơi chung với To An không
```

must not wake the bot. An explicit human vocative wins over generic words such as `bạn`.

## Registration truth

Chat never becomes registration truth. If Long or To An is not currently present in the selected session roster, the advisor explains that fact and does not add them. Poll/database state remains authoritative.

## Proposal state

The proposal is scoped by:

```text
GroupId + SenderZaloUserId
```

and stored as structured V2 state with a short TTL. It contains stable requester/partner UIDs and optional session ID/name. Follow-ups such as `T6` can therefore continue naturally without repeating names or tagging the bot.

## Rollout gates

This PR reuses the already-tested Ambient Fact Pilot send gates. Repository defaults remain safe:

```json
"ShadowMode": true,
"FactPilot": {
  "Enabled": false,
  "MinimumScore": 85
}
```

So merging code alone does not make the bot start replying to untagged group messages. Production enablement still requires the shadow review and explicit rollout configuration.

## Validation gate

The stacked PR is validated against the complete Ambient tree (`#34 + #35 + this phase`) before its base is restored. Tests must cover both the exact natural-language examples above and the negative human-thread/mutation cases.

## Out of scope

Social AI / banter / autonomous joking is intentionally deferred. This phase is limited to conversational addressing, capability explanation, stable-identity domain advice, session clarification, and read-only proposal state.
