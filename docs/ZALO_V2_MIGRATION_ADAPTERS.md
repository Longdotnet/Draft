# Zalo V2 legacy migration adapters

This branch finishes the compatibility work intentionally left partial by the V2 conversation/context kernel without rewriting the large legacy `ZaloBotService` router in one risky change.

## Provider message IDs

`ZaloBridgeClient` records a successful real provider `messageId` in the V2 message graph and creates a short-lived `ZaloOutboundReceipt` containing only:

- Zalo connection ID
- group ID
- provider message ID
- parent inbound message ID when the existing idempotency key proves it
- SHA-256 of the outbound text
- receipt timestamp

Raw outbound text is not copied into the receipt table. `ZaloLegacyOutboundCanonicalizer` changes a legacy core `ZaloGroupMessage.MessageId` from `bot:{guid}` to the provider ID only when exactly one synthetic bot row matches connection, group, content fingerprint, and a tight timestamp window. Ambiguous matches are deliberately left unchanged.

## Stable person identity before legacy routing

`ZaloLegacyIdentityMigrationAdapter` scans an addressed command for exact current-member display names and active user-approved `preferred_name` aliases. Each phrase is resolved through `ZaloIdentityResolver`.

Only a unique `Resolved` UID becomes a metadata-only `ZaloBridgeMention` (`Len = 0`). `Ambiguous` and `NotFound` results never create a mention. Existing mention-aware legacy handlers therefore receive stable Zalo UIDs without changing user-visible text.

An `IdentityPreRouting` trace stores only stable person keys, not the original prompt.

## Typed pending state

`ZaloLegacyPendingPayloadAdapter` converts handler-specific pending JSON into a bounded V2 envelope containing typed session/person/team identifiers, missing arguments, and candidate entities. Arbitrary legacy fields are not copied into V2 collected arguments.

For an addressed follow-up, `ZaloOverbookService.V2PreRouting` now applies this adapter in the **same webhook turn**, before the central topic-switch decision and before the legacy bot router runs. The next user turn therefore sees typed collected/missing/candidate state immediately instead of waiting for a maintenance cycle. Temporal filtering/order for legacy pending rows is evaluated in memory so SQLite and PostgreSQL follow the same `DateTimeOffset` semantics.

`ZaloLegacyPendingStateProjector` remains as background reconciliation for active legacy workflows. It refuses to overwrite an already-active V2 state for a different intent.

## Trace enrichment

The existing terminal legacy outcome projector stays responsible for exactly-once outcome rows. `ZaloLegacyTraceEnricher` later fills only IDs that are already known from authoritative V2 metadata:

- provider reply message ID from the message graph
- resolved person IDs from V2 pre-routing traces
- resolved session ID from an existing V2 trace when present

It does not infer or reconstruct business facts from raw chat text.

## Maintenance ordering

The existing maintenance worker runs migration work in this order:

1. canonicalize provider outbound IDs
2. reconcile typed pending state
3. project terminal legacy outcomes
4. enrich projected traces
5. run retention cleanup

Migration receipts use the message-relation retention cutoff and are deleted immediately once their provider ID has been canonicalized.

## Rollout rule

These adapters are transitional. After provider IDs, stable identity, structured state, and common trace fields are proven in production, the legacy compatibility paths should be removed instead of maintained as a second permanent architecture.
