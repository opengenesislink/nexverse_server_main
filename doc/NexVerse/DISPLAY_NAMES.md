# NexVerse Display Names

> Development line: **NexVerse 0.9.3.3 Dev**  
> Status: **foundation implemented**

## Identity model

NexVerse separates three name concepts:

- **principal UUID** — immutable identity key;
- **username** — unique login/script identity derived from the stored account name;
- **display name** — non-unique presentation name shown by compatible viewer surfaces.

For a modern `Resident` account, the default Display Name is the first name only. For a legacy two-part account, the default is `FirstName LastName`.

Changing a Display Name never renames the stored account and never changes login resolution.

## Persistence

Display Name state is stored in the authoritative `UserAccounts` datastore:

- `DisplayName`
- `DisplayNameChanged`

Migrations are provided for MySQL/MariaDB, PostgreSQL and SQLite.

An empty stored `DisplayName` means “use the default Display Name”. This avoids duplicating the derived default value and preserves correct behavior if older accounts are migrated.

## Validation and change policy

Display Names:

- are trimmed;
- may contain at most 31 characters;
- may not contain control characters;
- are not required to be unique.

Resident self-service changes use a seven-day cooldown. Re-submitting the current effective Display Name is treated as a no-op and does not restart the cooldown.

Administrative World API changes may bypass the self-service cooldown.

## World API

Existing user endpoints expose the Display Name state.

`GET /api/v1/users/{principalId}` and search results include:

- `username`
- `display_name`
- `is_display_name_default`
- `display_name_changed`
- `display_name_next_update`

`PATCH /api/v1/users/{principalId}` accepts:

```json
{
  "display_name": "Antonia"
}
```

An empty value resets to the account's default Display Name.

## Viewer capabilities

The simulator publishes:

- `GetDisplayNames`
- `SetDisplayName`

`GetDisplayNames` supplies username, Display Name, legacy first/last name, UUID, default-state flag, next-update timestamp and cache expiry.

`SetDisplayName` accepts the viewer-compatible `display_name` old/new array. Stale old-name requests produce a conflict reply, preventing a cached viewer from overwriting a newer name accidentally.

Successful updates publish:

- `SetDisplayNameReply` to the requesting agent;
- `DisplayNameUpdate` to connected presences in the region so their name caches can refresh.

## Login and LSL

LLLogin XML-RPC/LLSD responses expose:

- `username`
- `display_name`
- `is_display_name_default`

LSL functions use the same account data:

- `llGetDisplayName`
- `llRequestDisplayName`

Legacy identity functions such as `llGetUsername`, `llRequestUsername`, `llKey2Name` and `DATA_NAME` retain their identity/legacy-name semantics.

## Remaining 0.9.3.3 propagation

The foundation does not claim full Display Name completion yet. Remaining work includes profile/WebProfileV3 integration, chat/IM/group presentation, search and creator/object presentation where appropriate.
