# ZYRO Form ↔ Draft member mapping

This integration lets the ZYRO Form admin match a submitted Zalo display name to a member in a selected Draft Zalo group, review ambiguous matches, and send the confirmed member UID with the volleyball profile fields to Draft.

## Request flow

The browser must call the existing ZYRO Supabase Edge Function. That function calls Draft's internal API server-to-server. Never put the Draft mapping key in public JavaScript or browser storage.

1. The Form admin loads Draft groups.
2. The Form admin loads the selected group's member directory.
3. The Form admin compares the Form's `zalo_name` with `displayName` and `zaloName` from the directory. Exact unique matches can be preselected. Duplicate or approximate matches must be confirmed by an admin.
4. The Form backend sends the selected `zaloUserId` values and mapped gender, role, and level to Draft.
5. Draft verifies every UID against a fresh, complete directory for the selected connection and group, then fills missing `PlayerProfile` fields.

Names are candidate-search hints only. Draft writes profiles by verified Zalo UID and never trusts a display name or avatar supplied by the Form client.

## Draft internal API

All routes require the `x-zyro-mapping-key` header.

### `GET /api/internal/zyro-mapping/groups`

Returns connected Draft Zalo accounts and the groups visible to the configured mapping owner.

### `GET /api/internal/zyro-mapping/groups/{connectionId}/{groupId}/members`

Returns the selected group's member directory:

- `connectionId`, `connectionName`
- `groupId`, `groupName`
- `expectedMemberCount`, `isComplete`
- `members[]`: `zaloUserId`, `displayName`, `zaloName`, `avatarUrl`

The Form admin may show the directory when `isComplete` is false, but must disable profile sync until a complete directory is available.

### `POST /api/internal/zyro-mapping/profiles/sync`

Request body:

```json
{
  "connectionId": "draft-connection-id",
  "groupId": "zalo-group-id",
  "profiles": [
    {
      "zaloUserId": "verified-zalo-uid",
      "gender": "Male",
      "role": "Defense",
      "level": "Average"
    }
  ]
}
```

`gender`, `role`, and `level` use the Draft enum names. They may be `null` when the Form has no recognized value. A request can contain 1–200 profiles.

Recommended Form value mapping:

| ZYRO Form value | Draft value |
|---|---|
| Nam | `Male` |
| Nữ | `Female` |
| Công | `Attack` |
| Thủ | `Defense` |
| Chuyền 2 | `Setter` |
| Toàn diện | `FullStack` |
| Người mới | `New` |
| Trung bình | `Average` |
| Tốt | `Good` |

Draft currently consumes gender, playing position, and skill level for `PlayerProfile`. The Form's participation duration and free-text wishes remain in the Form registration record; they are not written into Draft's player profile.

Draft re-reads the group directory during sync and rejects the request when:

- the connection does not belong to the allowed Draft admin;
- the group is no longer available to that connection;
- the member directory is incomplete;
- a UID is not a current member of the selected group;
- the request contains duplicate UIDs or invalid enum values.

Draft creates a profile when the UID has no profile. For an existing profile it fills missing/default fields only: unknown gender, unassigned role, and missing level. `New` is a valid skill level and is preserved. Existing non-default values are returned in `preservedFields` for the Form admin to review. The current Zalo display name and avatar are used to initialize a new profile; the Form client cannot set them.

The Form's approval status is not part of this API contract and does not gate profile sync. The Form admin decides which completed registrations to map.

## Render configuration

Set these variables on the Draft API web service:

| Render variable | Required | Purpose |
|---|---:|---|
| `ZyroMapping__InternalKey` | Yes | Shared server-to-server key; at least 32 UTF-8 bytes |
| `ZyroMapping__AdminUserId` | Only if needed | Restricts mapping to one Draft admin when multiple Draft admins have connected Zalo accounts |

If `ZyroMapping__AdminUserId` is empty, Draft automatically selects the owner only when exactly one distinct admin has a connected Zalo account. If multiple owners exist, Draft fails closed with HTTP 503 until the owner ID is configured. When setting it, use the `User.Id` of the Draft admin who owns the Zalo connection.

Generate a random key, for example with `openssl rand -hex 32`. Store the same value in the Draft Render environment and the ZYRO Supabase Edge Function secret `ZYRO_MAPPING_INTERNAL_KEY`. Also set `DRAFT_API_BASE_URL` in the Edge Function to the Draft API origin. Do not commit either secret.

## Expected Form-side responsibilities

The ZYRO Edge Function should proxy these Draft requests and attach `x-zyro-mapping-key`. The Form admin page should:

- read registrations from `zyro_registrations`;
- show a mapping preview before syncing;
- preselect only a unique exact name match;
- require explicit selection for duplicate, approximate, or missing matches;
- persist the selected `zaloUserId` against the Form registration in the Form database;
- send only the confirmed UID and the mapped `gender`, `role`, and `level` to Draft.

The Draft API in this repository provides the trusted group directory and profile-sync boundary. The Form-side preview and persistence UI must be implemented in the ZYRO Form repository.
