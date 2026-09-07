# Pass/share-slot guidance invariant

When a user explicitly addresses NPC (mention or verified direct reply) and asks how to use pass/share slot, answer through deterministic contextual guidance before general AI routing.

Keep these boundaries:

- guidance is informational only and performs no domain mutation;
- pass/nhường means giving up one participation; ShareSlot means multiple people sharing one slot;
- teach only syntax already supported by deterministic handlers;
- self-pass/claim remains grounded in OpenSlotOffer authoritative state;
- pre-draft handoff remains poll/roster-authoritative;
- post-draft transfer remains revalidated and confirmation-gated;
- delegated transfer/share continues to enforce server-side permission and stable UID checks;
- action turns and pass-slot fact queries must not be stolen by the help recognizer;
- ordinary unmentioned group chatter must not wake contextual help.
