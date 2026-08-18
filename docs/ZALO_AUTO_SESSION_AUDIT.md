# Auto Session audit

The desktop audit panel is read-only and uses the authenticated `GET /api/zalo/auto-session-groups` response.

Each group includes up to 10 recent Auto Session proposal snapshots in `activity`. The snapshot exposes proposal status, classifier confidence/reason, organizer approval metadata, last error, parsed poll options, option-to-session links, current roster/capacity, session status, and the latest successful `PollImport.ImportedAt` timestamp.

The audit response never creates, retries, approves, cancels, or mutates a session. All operational writes continue through the existing Auto Session confirmation/settings/sync flows.
