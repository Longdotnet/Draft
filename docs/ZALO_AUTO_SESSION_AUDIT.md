# Auto Session audit

The desktop audit panel is read-only and uses the authenticated `GET /api/zalo/auto-session-groups` response.

Each group includes up to 10 recent Auto Session proposal snapshots in `activity`. The snapshot exposes proposal status, classifier confidence/reason, organizer approval metadata, last error, parsed poll options, option-to-session links, current roster/capacity, session status, and the latest successful `PollImport.ImportedAt` timestamp.

For linked sessions the audit also reads the existing overbook state and shows `EffectiveSlotCount`, `ExcessSlotCount`, and whether voter order still needs organizer confirmation. The UI therefore shows over-capacity from the same domain state used by the reminder engine rather than guessing from raw roster size; shared or reserved slots do not produce a misleading badge.

The overbook audit path is a DDL-free SELECT. If `ZaloOverbookStates` has not been initialized yet, the audit returns no overbook state and does not create, migrate, or upgrade that schema from a GET request.

The audit response never creates, retries, approves, cancels, or mutates a session. All operational writes continue through the existing Auto Session confirmation/settings/sync flows.
