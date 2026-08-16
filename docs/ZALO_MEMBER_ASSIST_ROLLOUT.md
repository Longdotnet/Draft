# Rollout notes

This change is intentionally compatible with the current live ambient configuration:

- Member-assist executes only when ambient mode is enabled and `ShadowMode=false`.
- The pass-slot helper is read-only and therefore can speak proactively without granting mutation authority.
- Explicit TeamPreference/ShareSlot mutations remain on the existing confirmation/domain path.
- Permission grant/revoke requires an explicit bot-addressed command and a successful live Zalo group-role authorization check.
- Short wake phrases use deterministic casual copy before Social AI.

If production precision needs to be reduced temporarily, set `ZaloBot:Ambient:MemberAssist:Enabled=false`; the rest of ambient Fact/Social behavior remains unchanged.
