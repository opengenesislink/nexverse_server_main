# NexVerse World API

The NexVerse World API is the new Robust-hosted control-plane API.

Current development endpoint:

`http://world.stadt-nexverse.de/api/v1`

NexVerse currently runs Robust directly on TCP port 80 without a reverse proxy. Therefore `world.stadt-nexverse.de` should resolve to the same Robust host as the public grid endpoint. The World API uses its own `/api/v1` path namespace.

## Public foundation endpoints

- `GET /api/v1` — service metadata
- `GET /api/v1/health` — API health
- `GET /api/v1/version` — NexVerse/API/protocol versions
- `GET /api/v1/capabilities` — compatibility/capability levels
- `GET /api/v1/openapi.json` — OpenAPI 3.1 document

These endpoints are public and read-only.

## Privileged user API foundation

The authenticated user-management foundation now implements:

- `GET /api/v1/regions?q=<query>` — administrator search for selectable home/start regions; with no query it returns configured default regions
- `GET /api/v1/users/me` — authenticated resident account
- `GET /api/v1/users?q=<query>` — administrator account search
- `POST /api/v1/users` — administrator account creation and provisioning
- `GET /api/v1/users/{principalId}` — self or administrator account lookup
- `PATCH /api/v1/users/{principalId}` — account profile update; self may update email/country, administrators may also update the user title
- `PATCH /api/v1/users/{principalId}/level` — administrator UserLevel update
- `POST /api/v1/users/{principalId}/password` — self-service password change or administrator reset

Account creation uses a JSON body such as:

```json
{
  "first_name": "Jam",
  "last_name": "Resident",
  "email": "jam@example.invalid",
  "password": "replace-with-a-strong-password",
  "home_region": {
    "id": "00000000-0000-0000-0000-000000000000",
    "position": {
      "x": 128,
      "y": 128,
      "z": 25
    }
  }
}
```

A home/start region is mandatory for account creation. The caller may select it by region UUID or exact region name. The optional position defaults to `128,128,25` and is validated against the selected region size.

The selected region is written both as the avatar's Home location and as its initial Last/Start position. This ensures a new avatar starts in the selected region whether the viewer requests Home or Last Location on the first login.

The provisioning response reports authentication, inventory, home and initial start-position initialization. A created account is considered ready only when all four required steps succeeded.

Profile updates accept `email`, `user_country` and, for administrators, `user_title`.

Password updates use:

```json
{
  "new_password": "replace-with-a-strong-password"
}
```

The UserLevel request body is:

```json
{
  "user_level": 200
}
```

The API does not access the database directly. It uses a NexVerse `INexUserService` abstraction backed by the existing `IUserAccountService` while the identity subsystem is being migrated.

## Interim authentication bridge

The current transition layer validates an existing AuthenticationService token.

Required request headers when privileged endpoints are enabled:

```text
Authorization: Bearer <authentication-service-token>
X-NexVerse-Principal: <principal-uuid>
```

The token is verified by `IAuthenticationService.Verify`. The corresponding user account is then loaded from `IUserAccountService`.

For the initial bridge, an account at or above `AdminMinimumUserLevel` receives the NexVerse `admin:*` scope. The default minimum is 200.

This is not the final NexVerse identity model. The roadmap target remains a NexVerse-native scoped token/OIDC-style authentication system.

## Transport safety

Privileged API endpoints are **disabled by default**:

```ini
[NexVerseWorldApi]
EnablePrivilegedEndpoints = false
```

This is intentional because the current production Robust endpoint is plain HTTP.

Bearer tokens must not be enabled on a public plaintext transport. Privileged endpoints should only be enabled once the API is protected by TLS or an equivalent trusted/private transport.

No password-login endpoint is exposed by the World API over HTTP.

## Architecture

The World API is implemented in the NexVerse-native `NexVerse.Server.Api` assembly and depends on `NexVerse.Core`.

The current foundation provides:

- versioned `/api/v1` namespace;
- OpenAPI 3.1 description;
- RBAC/scope contracts;
- user-service abstraction;
- audit events;
- NexBus event publication;
- correlation IDs;
- API version response headers;
- citizen/admin AI metadata;
- interim AuthenticationService token validation;
- initial authenticated user-management routes.

## Audit and events

Administrative changes emit audit information and NexBus events.

Current user mutations emit audit and NexBus events:

- account creation: `users.create` / `user.created`
- account profile update: `users.update` / `user.updated`
- password update: `users.password.update` / `user.password.changed`
- UserLevel update: `users.level.update` / `user.level.changed`

Passwords are never written to audit records or NexBus payloads.

## CI runtime verification

NEXJAST CI starts a minimal Robust process using `bin/Robust.NexVerseApi.Tests.ini` and verifies live HTTP responses from:

- `/api/v1/health`
- `/api/v1/version`
- `/api/v1/openapi.json`

The smoke-test configuration keeps all privileged endpoints disabled and has no production database dependency.

## Next API work

The next user-management layer must add, behind scoped authentication:

- soft deletion/deactivation;
- lock/unlock;
- ban/unban;
- durable account audit/history;
- proper NexVerse token issuance and scope assignment;
- rate limiting and production security controls.
