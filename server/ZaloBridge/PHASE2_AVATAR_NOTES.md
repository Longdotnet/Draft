# Phase 2 avatar source upgrade

`getMembers` keeps the group-member thumbnail as a safe fallback, then asks zca-js 2.1.2 for progressively better profile imagery:

1. `getFullAvatar` (`full_avatar`)
2. `getFullAvatar` (`bk_full_avatar`)
3. `getAvatarUrlProfile(..., 240)`
4. existing group-member avatar

Full-avatar requests are bounded to four concurrent calls and failures remain best-effort so member resolution and bot commands continue working when Zalo restricts a profile image.

Poster 01 diagnostics remain enabled in the backend; after deployment, `Poster01AvatarDiagnostic` is the source of truth for the actual decoded dimensions received by SkiaSharp.
