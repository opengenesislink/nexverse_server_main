# NexVerse World API

The NexVerse World API is the new Robust-hosted control-plane API.

Active server development line: **NexVerse 0.9.3.2 Dev — World API v1**

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

## Statistics endpoint and privacy levels

`GET /api/v1/statistics/summary` is registered independently of `EnablePrivilegedEndpoints`.

Without credentials, the endpoint returns a privacy-safe aggregate view when the configured account/GridUser data services are available. Aggregate mode contains headline counts such as registered residents, current total presence, recent activity and Hypergrid totals, but deliberately omits individual names, principal IDs, concrete online-region rows and Hypergrid home-grid detail lists.

When privileged World API authentication is enabled, a Bearer token or `X-NexVerse-Api-Key` with `statistics:read` may request the protected detail view. Protected credentials must only be used over TLS or an equivalent trusted transport.

If credentials are supplied while privileged authentication is disabled, the server returns a controlled service/authentication error rather than silently ignoring the credential or falling back to an anonymous response. A missing statistics data backend returns `503 statistics_data_unavailable`; the route itself must not disappear as a 404 merely because privileged endpoints are disabled.

The response reports its effective privacy level through:

- `detail_level=aggregate|authenticated`
- `protected_details=false|true`

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

## NexVerse native resident sessions and access tokens

NexVerse now uses native signed/scoped access tokens for World API resident authentication. Native tokens use an HMAC-SHA256 signed JWT-shaped format containing issuer, audience, subject, issue/expiry timestamps, token ID, scopes and an account security stamp.

Resident bootstrap is native:

`POST /api/v1/auth/session`

Request body:

```json
{
  "username": "Antonia.Porta",
  "password": "resident-password"
}
```

Single-name residents may use their normal Resident-compatible login name. The server resolves the canonical NexVerse resident identity, verifies the password internally against the existing credential store, and directly issues a NexVerse access token. It does not mint or expose an AuthenticationService bearer token during this flow.

The response contains `access_token`, `token_type=Bearer`, `expires_in`, the granted scope set, the resident principal UUID and the request correlation ID. Token responses use `Cache-Control: no-store`.

The retired bootstrap endpoint `POST /api/v1/auth/token` and the transitional `X-NexVerse-Principal` header are no longer accepted by the World API. Existing AuthenticationService bearer tokens are not valid World API credentials.

Native token issuance is disabled by default and requires `EnableNativeTokens=true` plus a protected signing key of at least 32 UTF-8 bytes. The signing key must not be committed to the repository.

A native token is bound to the account lifecycle security stamp. Lock, ban, deactivation, unlock, unban, reactivation, password change and explicit session revocation advance that stamp and invalidate older resident sessions.

Administrators at or above `AdminMinimumUserLevel` receive the NexVerse `admin:*` scope. The default minimum is 200. Normal resident bootstrap sessions receive `users:read` and `users:write`.

World API bearer authentication now accepts only native NexVerse access tokens. Restricted machine integrations use `X-NexVerse-Api-Key`; OAuth service clients use `client_credentials`.

## Transport safety

Privileged API endpoints are **disabled by default**:

```ini
[NexVerseWorldApi]
EnablePrivilegedEndpoints = false
```

This is intentional because the current production Robust endpoint is plain HTTP.

Bearer tokens must not be enabled on a public plaintext transport. Privileged endpoints should only be enabled once the API is protected by TLS or an equivalent trusted/private transport.

The resident session endpoint carries a password and therefore must never be exposed over public plaintext HTTP. It is available only with privileged World API endpoints and must be protected by TLS or an equivalent trusted transport.

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
- native resident session authentication without a legacy bearer-token bridge;
- authenticated user-management routes.

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

NexVerse CI starts a minimal Robust process using `bin/Robust.NexVerseApi.Tests.ini` and verifies live HTTP responses from:

- `/api/v1/health`
- `/api/v1/version`
- `/api/v1/openapi.json`
- `/api/v1/statistics/summary`

The smoke-test configuration keeps all privileged endpoints disabled and has no production database dependency. Because that profile intentionally has no statistics database services, the statistics request is expected to return `503 statistics_data_unavailable`; CI explicitly verifies that the route exists and does not regress to the former 404 behavior.

## Region-grid administration API

The first read-only Region Control Plane foundation is implemented and is used as the data contract for the later administrator raster planner.

Implemented endpoints:

- `GET /api/v1/grid/layout?min_x=<x>&max_x=<x>&min_y=<y>&max_y=<y>` — bounded world-grid layout and per-cell occupancy data
- `GET /api/v1/grid/cells/{x}/{y}` — exact cell inspection
- `GET /api/v1/grid/validate-placement?x=<x>&y=<y>&size_x=<meters>&size_y=<meters>` — overlap/placement validation

These operations require `regions:read`. Grid coordinates are 256m base cells; responses also expose absolute world-meter coordinates. Layout windows are capped at 128x128 cells. VarRegions occupy every base cell touched by their actual width and height, so a 512x512m region occupies four cells. Partial overlap is rejected by the placement validator.

Cells can currently report `free`, `occupied`, `reserved` or `conflict`. Existing GridService reservation flags are respected, and the historical low-Y band reserved by GridService for Hypergrid links is surfaced as reserved.

Planned mutation endpoints:

- `POST /api/v1/regions` — create a region at validated coordinates
- `PATCH /api/v1/regions/{regionId}/placement` — move a region after validation

The corresponding admin UI at `world.stadt-nexverse.de` will render this data as an interactive raster map. Hovering a cell must show exact grid coordinates and absolute world coordinates. Free, occupied, reserved and unavailable cells must be visually distinct.

VarRegions are treated as multi-cell footprints. A 512x512m region, for example, occupies four 256x256m grid cells, and all of those cells must be considered occupied during placement validation.

Selecting a free cell in the raster will prefill the create-region form. Region-size selection should show the intended footprint before creation and refuse any overlap.

## Next API work

The core user lifecycle, persistent audit history, native session revocation, OAuth/OIDC client flows, API keys and request rate limiting are connected.

The browser-facing authorization/login and consent surface is now implemented. The next larger API/control-plane work is the Region Control Plane and its grid-layout administration APIs; MFA/passkey work remains a later identity-security extension.


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

The authorization endpoint supports two compatible interactive modes. Existing technical clients may continue to call `GET /oauth/authorize` with an authenticated native NexVerse resident Bearer session. Normal browsers without a Bearer header receive a German NexVerse login and consent page and submit the approval to the same `/oauth/authorize` endpoint.

The browser flow validates the registered client, exact redirect URI, PKCE S256 challenge and requested scopes before showing the login page and validates them again on POST. Resident credentials are verified through the NexVerse user-service abstraction; locked, banned, deactivated and incomplete provisioning accounts are rejected. Passwords are never included in redirects, audit records, HTML responses or browser storage.

Approval creates the normal one-time authorization code and redirects to the registered client with `code` and preserved `state`. Denial redirects with `error=access_denied`. Successful approvals emit an audit event and NexBus event. The authorization page is delivered with `Cache-Control: no-store`, frame blocking, no-referrer policy and a restrictive CSP. Legacy AuthenticationService bearer tokens remain rejected.


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


## Idempotent user provisioning

`POST /api/v1/users` supports the `Idempotency-Key` request header.

A validated create request reserves the key inside the authenticated administrator/endpoint scope. The default retention is 86400 seconds and can be changed with:

```ini
IdempotencyStorePath = "data/nexverse-idempotency.json"
IdempotencyTtlSeconds = 86400
```

Behavior:

- first request: provisioning executes and `Idempotency-Replayed: false` is returned;
- same key and equivalent JSON payload: the persisted original HTTP response is replayed with `Idempotency-Replayed: true`;
- same key with a different payload: HTTP `409 idempotency_key_conflict`;
- concurrent duplicate while the first request is executing: HTTP `409 idempotency_in_progress` with `Retry-After: 1`;
- pending in-process reservations are discarded after a process restart; completed responses are persisted.

The idempotency store never contains plaintext passwords separately. Its request fingerprint is SHA-256 over the validated JSON request representation; persisted response data contains only the normal API response.


## Restricted machine API keys

NexVerse supports persistent machine credentials through the `X-NexVerse-Api-Key` header.

Administrative management endpoint:

- `GET /api/v1/auth/api-keys`
- `POST /api/v1/auth/api-keys`
- `PATCH /api/v1/auth/api-keys`

All three management operations require `admin:*` through a resident/OAuth administrator credential. API keys themselves can never receive `admin:*`, wildcard or interactive OIDC scopes.

A creation body is:

```json
{
  "name": "Region dashboard",
  "scopes": ["regions:read"]
}
```

The full key is returned exactly once. Its secret is persisted only as a PBKDF2-SHA256 hash. Runtime storage defaults to:

`data/nexverse-api-keys.json`

Supported machine scopes are explicit NexVerse functional scopes such as `regions:read`, `regions:manage`, `users:read`, `inventory:read`, `estates:manage`, `economy:read` and their documented write/transfer counterparts.

API-key principals use subjects in the form `api-key:<key_id>`. Disabling a key takes effect immediately because validation is performed against the persistent key store on every request.

The selectable-region endpoint now requires `regions:read` rather than `admin:*`, making it usable by restricted machine integrations while administrator bearer tokens continue to satisfy the scope through their administrative wildcard.


## OpenAPI 3.1 schema contract

The runtime OpenAPI document now publishes reusable component schemas for the primary v1 models rather than endpoint summaries only.

Current schema coverage includes:

- standardized `Error` and `Pagination`;
- `User`, `UserCreateRequest`, `UserCreateResponse` and profile/lifecycle request bodies;
- `ResidentSessionRequest` and `ResidentSessionResponse` for native resident login;
- `Region` and `RegionSearchResponse`;
- `ApiKey`, API-key create/state requests and list/create responses;
- `AuditEvent` and paginated audit search responses.

Core operations reference their request and response schemas directly through `#/components/schemas/...`. User-ID routes declare the `principalId` UUID path parameter, while search endpoints document their actual query parameters and bounds. `POST /api/v1/users` also documents `Idempotency-Key`.

NexVerse CI starts a real Robust process, downloads `/api/v1/openapi.json`, parses the JSON document and verifies these component/ref contracts at runtime.


## Self-hosted API documentation and explorer

NexVerse serves a dependency-free API documentation UI at:

`/api/v1/docs`

The page loads the live `/api/v1/openapi.json` document from the same running Robust instance. It provides:

- searchable endpoint catalogue by path, method, summary and scope;
- live display of path/query/header parameters;
- resolved JSON request schemas and response/error schemas;
- authentication and scope badges;
- automatic example JSON bodies derived from the OpenAPI schema;
- same-origin live request execution;
- in-memory Bearer token and `X-NexVerse-Api-Key` fields;
- optional `Idempotency-Key` support;
- response status, correlation ID, headers and formatted JSON body;
- deprecation indicator when an operation is marked deprecated.

The explorer has no CDN or third-party JavaScript dependency. Credentials entered into the page are not persisted by the page. The docs response uses `Cache-Control: no-store`, a restrictive same-origin Content Security Policy and `Referrer-Policy: no-referrer`.


## Version history and endpoint automation metadata

The OpenAPI root now publishes `x_nexverse_version_history`. The self-hosted explorer renders that history directly, keeping the visible documentation tied to the running server contract.

Every generated World API operation also carries endpoint-level NexVerse metadata:

- `x-nexverse-audience` — citizen, admin and/or service;
- `x-nexverse-purpose` — concise operation purpose;
- `x-nexverse-ai-instruction` — guidance for ChatGPT/API-agent use;
- `x-nexverse-security-constraints` — credential, authorization, audit and idempotency constraints where applicable;
- `x-nexverse-scope` — explicit scope when the operation has one.

Administrative operations explicitly state that authorization must never be inferred or escalated. Self-service operations instruct clients to default to the authenticated resident subject. Scoped service operations require the documented functional scope.

The API explorer renders these fields for the selected operation and surfaces the standard OpenAPI `deprecated` flag when present.
