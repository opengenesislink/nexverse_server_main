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
- `DELETE /api/v1/users/{principalId}` — administrator soft-delete/deactivation; persisted data is retained
- `PATCH /api/v1/users/{principalId}/state` — administrator lock, ban, deactivate or reactivate workflow
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

Account provisioning is fail-closed. A new database record is first written as inactive with state `provisioning`. Authentication, inventory, Home and initial Last/Start position are then initialized. Only when all mandatory steps succeed is the record finalized as `active`; otherwise it becomes `provisioning_failed` and viewer/API login remains blocked.

The provisioning response reports authentication, inventory, home, initial start-position and state-finalization results. `ready=true` is emitted only for a finalized active account.

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

Account lifecycle transitions use:

```json
{
  "state": "locked",
  "reason": "Administrative account lock"
}
```

Supported administrator states are `active`, `locked`, `banned` and `deactivated`. Setting `active` performs unlock, unban or reactivation. A reason is mandatory for every blocking/deactivation transition and is capped at 255 characters. The internal `provisioning` and `provisioning_failed` states cannot be selected through the administrator endpoint.

`DELETE /api/v1/users/{principalId}` deliberately performs a soft delete by switching the account to `deactivated`; identity, inventory and audit references are not physically deleted.

The API does not access the database directly. It uses a NexVerse `INexUserService` abstraction backed by the existing `IUserAccountService` while the identity subsystem is being migrated.

## NexVerse native access-token transition

NexVerse now has a native signed/scoped access-token foundation. Native tokens use an HMAC-SHA256 signed JWT-shaped format containing issuer, audience, subject, issue/expiry timestamps, token ID, scopes and an account security stamp.

When native tokens are enabled, an existing AuthenticationService bearer token can be exchanged at:

`POST /api/v1/auth/token`

The exchange request uses the transitional headers:

```text
Authorization: Bearer <authentication-service-token>
X-NexVerse-Principal: <principal-uuid>
```

The response returns a NexVerse `access_token`, `token_type=Bearer`, `expires_in` and the granted scope set. Subsequent World API requests using the NexVerse token no longer require `X-NexVerse-Principal`.

Native token issuance is disabled by default and requires `EnableNativeTokens=true` plus a protected signing key of at least 32 UTF-8 bytes. The signing key must not be committed to the repository.

A native token is bound to the account lifecycle security stamp. Lock, ban, deactivation, unlock, unban and reactivation advance that stamp monotonically, invalidating tokens issued against an older account state.

### Interim bootstrap bridge

The current transition layer still accepts an existing AuthenticationService token as a bootstrap mechanism while full NexVerse OIDC/client authorization is developed.

Required request headers when privileged endpoints are enabled:

```text
Authorization: Bearer <authentication-service-token>
X-NexVerse-Principal: <principal-uuid>
```

A legacy bootstrap token is verified by `IAuthenticationService.Verify`. A native NexVerse token is verified cryptographically and its subject is loaded from `IUserAccountService`. Tokens belonging to locked, banned, deactivated or incomplete-provisioning accounts are rejected. Native tokens with a stale account security stamp are also rejected.

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
- lifecycle transition: `users.state.update` / `user.state.changed`
- soft deletion/deactivation: `users.deactivate` / `user.deactivated`

Passwords are never written to audit records or NexBus payloads.

## CI runtime verification

NEXJAST CI starts a minimal Robust process using `bin/Robust.NexVerseApi.Tests.ini` and verifies live HTTP responses from:

- `/api/v1/health`
- `/api/v1/version`
- `/api/v1/openapi.json`

The smoke-test configuration keeps all privileged endpoints disabled and has no production database dependency.

## Planned region-grid administration API

The Region Control Plane will expose a raster-layout API used by the administrator web interface.

Planned endpoints:

- `GET /api/v1/grid/layout?min_x=<x>&max_x=<x>&min_y=<y>&max_y=<y>` — bounded world-grid layout and occupancy data
- `GET /api/v1/grid/cells/{x}/{y}` — cell inspection
- `GET /api/v1/grid/validate-placement?x=<x>&y=<y>&size_x=<meters>&size_y=<meters>` — overlap/placement validation
- `POST /api/v1/regions` — create a region at validated coordinates
- `PATCH /api/v1/regions/{regionId}/placement` — move a region after validation

The corresponding admin UI at `world.stadt-nexverse.de` will render this data as an interactive raster map. Hovering a cell must show exact grid coordinates and absolute world coordinates. Free, occupied, reserved and unavailable cells must be visually distinct.

VarRegions are treated as multi-cell footprints. A 512x512m region, for example, occupies four 256x256m grid cells, and all of those cells must be considered occupied during placement validation.

Selecting a free cell in the raster will prefill the create-region form. Region-size selection should show the intended footprint before creation and refuse any overlap.

## Next API work

The core user lifecycle is now connected. The next identity/API work is:

- durable account audit/history queries;
- password-change/manual session revocation for already issued native tokens;
- full OIDC authorization/client flows and persistent client registration;
- rate limiting and production security controls.


## OAuth 2.0 / OpenID Connect identity foundation

When `EnableNativeTokens=true`, NexVerse now exposes a persistent OAuth 2.0 / OpenID Connect foundation.

Public protocol endpoints:

- `GET /.well-known/openid-configuration` — OIDC discovery metadata
- `GET /oauth/jwks` — public ES256 JSON Web Key Set
- `GET /oauth/authorize` — Authorization Code flow with mandatory PKCE S256
- `POST /oauth/token` — `authorization_code`, `refresh_token` and `client_credentials`
- `POST /oauth/revoke` — access- or refresh-token revocation

Administrative identity endpoints:

- `GET /api/v1/auth/clients` — list registered OAuth/service clients
- `POST /api/v1/auth/clients` — register public, confidential or service clients
- `PATCH /api/v1/auth/clients` — enable/disable a client and advance its security stamp
- `POST /api/v1/auth/sessions/revoke` — revoke resident refresh sessions and invalidate current access tokens

Interactive clients use Authorization Code + PKCE. Public clients have no client secret. Confidential clients use a generated secret which is returned only at registration time. Service accounts use the `client_credentials` grant and receive subjects in the form `service:<client_id>`.

Client secrets are persisted only as PBKDF2-SHA256 hashes. Authorization codes and refresh tokens are persisted only as hashes. Refresh tokens rotate on every successful refresh. Individual native access tokens can be revoked by JTI.

OIDC ID tokens are signed with ES256 using a persistent P-256 key. The private key is generated at the configured `OidcSigningKeyPath`; only the public key is exposed through JWKS. Runtime-generated auth state and private keys are excluded from Git.

Resident password changes, account lock/ban/deactivation and explicit session revocation advance the resident security stamp. Existing native access tokens therefore fail validation and all persisted refresh sessions for the resident are revoked.

The authorization endpoint currently expects an already authenticated NexVerse/legacy bearer session. A browser consent/login surface and complete replacement of the legacy bootstrap path remain separate roadmap work.


## API rate limiting and request metadata

World API and OAuth HTTP handlers are protected by a per-client fixed-window limiter before authentication is processed. The default profile permits 240 requests per 60 seconds per client address.

Configuration:

```ini
[NexVerseWorldApi]
RateLimitEnabled = true
RateLimitRequests = 240
RateLimitWindowSeconds = 60
TrustForwardedFor = false
```

Every guarded response exposes a NexVerse API version and correlation identifier. Rate-limited responses use HTTP `429 Too Many Requests`, include `Retry-After`, and expose both standard-style `RateLimit-*` and compatibility `X-RateLimit-*` headers.

`X-Forwarded-For` is ignored by default to prevent clients spoofing their limiter identity. `TrustForwardedFor=true` may only be enabled when Robust is reachable exclusively through a trusted reverse proxy which overwrites that header.

Error responses from the privileged World API and OAuth endpoints include the same request correlation ID as the response header.

## Search pagination

User and selectable-region searches now support:

- `limit` — 1 through 100, default 50
- `offset` — 0 through 10000, default 0

Examples:

`GET /api/v1/users?q=resident&limit=25&offset=0`

`GET /api/v1/regions?q=NexVerse&limit=50&offset=0`

Search responses include:

```json
{
  "pagination": {
    "limit": 25,
    "offset": 0,
    "returned": 25,
    "has_more": true,
    "next_offset": 25
  }
}
```

The current v1 foundation uses bounded offset pagination. Large high-churn collections may move to cursor pagination in a later API revision without removing the current bounded contract.


## Persistent administrative audit history

Privileged World API and OAuth administrative actions are written to an append-only JSON Lines audit store in addition to the normal server log.

Default runtime path:

`data/nexverse-audit.jsonl`

The path can be changed with `AuditStorePath` in `[NexVerseWorldApi]`. Runtime audit data is excluded from Git.

Administrative query endpoints:

- `GET /api/v1/audit`
- `GET /api/v1/users/{principalId}/audit`

Both require `admin:*` and use the normal `limit`/`offset` pagination contract.

The global endpoint supports exact `resource` and `actor` filters. `action` may be exact or use a trailing wildcard, for example:

`GET /api/v1/audit?action=users.*&resource=<principal-uuid>&limit=50&offset=0`

Audit records contain event ID, timestamp, actor, action, resource, correlation ID and non-secret details. Passwords, access tokens, refresh tokens and OAuth client secrets are not written into audit details.


## Search filtering and sorting

User search supports the optional account-state filter:

`state=active|locked|banned|deactivated|provisioning|provisioning_failed`

User sort fields:

`sort=name|created|user_level|state`

Selectable-region sort fields:

`sort=name|size_x|size_y`

Both searches accept `order=asc|desc` and default to ascending name order.

The internal bounded search window now matches the public pagination contract through offset 10000; the earlier 100-result backend cap has been removed for these API searches.
