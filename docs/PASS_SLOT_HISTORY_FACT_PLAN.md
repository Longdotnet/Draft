# Pass-slot history facts

Pass-slot questions are read-only facts backed by durable `ZaloOpenSlotOffers` state. Chat text only determines query intent/scope; counts, people, sessions and statuses come from backend state.

Scopes are intentionally distinct:

- `EventToday`: offers created today in Vietnam time.
- `SessionToday`: offers belonging to sessions played today, even when the pass was announced earlier.
- `CurrentOpen`: only offers still open with no claimant.
- `SpecificSession`: historical offers for a referenced T2-T7/CN session.

People counts use distinct owner Zalo identities while slot counts use distinct offers. Completed/cancelled/expired offers remain visible in history summaries so a resolved pass does not disappear from an answer about what happened earlier today.
