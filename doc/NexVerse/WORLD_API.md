# NexVerse World API

The NexVerse World API is the Robust-hosted control-plane API for the NexVerse grid deployment running on OpenGenesisLINK.

Active server development line: **OpenGenesisLINK v0.9.3.7 Dev — NexGroups, NexExperiences and Experience LSL**

World API v1 was completed as the 0.9.3.2 development checkpoint on 3 October 2026 and remains the control-plane API foundation for the active OpenGenesisLINK development line. NexVerse continues as the deployment/grid identity.

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

## LunaTexture diagnostics projection

LunaTexture keeps detailed recovery diagnostics on the simulator where the rendering failure occurs. Each enabled Warp3D region persists a bounded diagnostic store under `LunaTextureDiagnosticStore` (default: `data/lunatexture`) using per-region JSON files, store format version `1`, and atomic file replacement. `nex texture clear` clears both the in-memory state and the persistent region store.

The NodeAgent heartbeat projects only aggregate diagnostic data to Robust:

- diagnostic texture record count;
- total observed occurrence count;
- number of affected regions;
- most recent diagnostic timestamp;
- occurrence counts grouped by LunaTexture classification.

Detailed prim names, positions and diagnostic reasons are not transmitted through NexBus. `GET /api/v1/nodes` exposes a `luna_texture` aggregate across online nodes and each node object contains its own `luna_texture` summary. `GET /api/v1/nodes/{nodeId}` exposes the same node-level summary. These endpoints remain protected by `simulators:read`.

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

Profile updates accept `email`, `user_country`, `display_name` and, for administrators, `user_title`.

Display Names are persisted separately from the immutable login identity. User payloads expose `username`, `display_name`, `is_display_name_default`, `display_name_changed` and `display_name_next_update`. A normal resident may change their own Display Name once per seven-day window; the API returns `429 display_name_cooldown` and `Retry-After` if the next update time has not been reached. Administrators may change a resident Display Name without the self-service cooldown. An empty Display Name resets the account to its default presentation name.

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

## Simulator NodeAgent registry

Robust maintains a live control-plane projection from NexBus NodeAgent events. The registry is rebuilt from simulator heartbeats and lifecycle events after a Robust restart rather than persisting a second potentially stale source of truth.

Protected endpoints:

- `GET /api/v1/nodes` — list observed simulator nodes
- `GET /api/v1/nodes/{nodeId}` — inspect one simulator node

Both require `simulators:read`. Responses expose node ID, hostname, OpenGenesisLINK server version, online/stale/offline state, uptime, process ID, working-set memory, CPU time, region/agent counts, heartbeat timestamps, the currently reported region list and a privacy-bounded `luna_texture` diagnostic summary. The list endpoint additionally aggregates LunaTexture data across currently online simulator nodes. The default stale threshold is 90 seconds and can be changed with `NodeStaleAfterSeconds` in `[NexBus]`.

The API Control Center exposes the same protected data in a dedicated **Simulatoren** view with online/stale/offline counts, NodeAgent transport status, process/resource information and per-node region details. Credentials entered there remain in the open page only and are not persisted.

The separate `simulators:manage` scope is defined for the later command plane. No start/stop/restart or arbitrary console execution is exposed by this registry milestone.

Grid-layout region records include `node_id` and `node_state` when ownership can be resolved from the live registry. Operational host/process details remain available only through the simulator API and are not copied into the `regions:read` response.

## Cross-region object messaging

OpenGenesisLINK 0.9.3.5 adds an opt-in remote fallback for the existing LSL `llRegionSayTo` behavior. Local targets remain completely local. Only when the requested UUID is not present as an avatar or scene object in the current region does the script API ask the NodeAgent's cross-region router to publish an `object.message.requested` NexBus event.

The receiving simulator scans only its currently loaded scenes for the exact target object UUID and injects the message through the normal `IWorldComm.DeliverMessageTo` path. A receiving script therefore continues to observe an ordinary `listen` event with the original source object UUID, source object name, channel and message. No second scripting API is required.

The feature is disabled by default with `CrossRegionObjectMessaging=false` in `[NexVerseNodeAgent]`. It is intended only for a trusted OpenGenesisLINK NexBus topology protected by HMAC plus TLS/private networking. It is not a permission bypass for arbitrary Hypergrid peers.

Safety defaults are deliberately restrictive:

- `AllowCrossOwnerObjectMessages=false` requires source and target objects to have the same owner UUID;
- `ObjectMessagesPerSecond=20` applies a fixed per-source-object rate limit on both outbound and inbound delivery;
- `ObjectMessageMaxAgeSeconds=30` drops delayed/replayed events outside the accepted time window in addition to NexBus EventId deduplication;
- message payloads retain the existing `llRegionSayTo` 1023-character limit;
- the LSL debug channel is never routed remotely;
- the source object and owner are revalidated against the local source scene before publication.

Successful remote delivery emits `object.message.received` with object, region, node and request-event identifiers. The initial implementation intentionally uses UUID fan-out across configured NexBus peers; a later object-directory optimization may reduce fan-out without changing the LSL contract.

The protected Node API projects the effective simulator capability as `cross_region_object_messaging`, `cross_owner_object_messaging`, `object_messages_per_second` and `object_message_max_age_seconds`. Its health object also reports `accepting_remote_object_messages`, so administrators can distinguish configured policy from a node that is currently offline or stale.

## Region-grid administration API

The first read-only Region Control Plane foundation is implemented and is used as the data contract for the later administrator raster planner.

Implemented endpoints:

- `GET /api/v1/grid/layout?min_x=<x>&max_x=<x>&min_y=<y>&max_y=<y>` — bounded world-grid layout and per-cell occupancy data
- `GET /api/v1/grid/cells/{x}/{y}` — exact cell inspection
- `GET /api/v1/grid/validate-placement?x=<x>&y=<y>&size_x=<meters>&size_y=<meters>` — overlap/placement validation

These operations require `regions:read`. Grid coordinates are 256m base cells; responses also expose absolute world-meter coordinates. Layout windows are capped at 128x128 cells. VarRegions occupy every base cell touched by their actual width and height, so a 512x512m region occupies four cells. Partial overlap is rejected by the placement validator.

Cells can currently report `free`, `occupied`, `reserved` or `conflict`. Existing GridService reservation flags are respected, and the historical low-Y band reserved by GridService for Hypergrid links is surfaced as reserved.

Managed mutation endpoints are now implemented:

- `POST /api/v1/regions` — queue creation of a NexVerse-managed region on an explicitly selected online NodeAgent
- `PATCH /api/v1/regions/{regionId}/placement` — queue a placement change for an already running NexVerse-managed region
- `POST /api/v1/regions/{regionId}/lifecycle` — queue `start`, `stop` or `restart` for a NexVerse-managed region
- `GET /api/v1/region-operations/{operationId}` — read the asynchronous `queued`, `accepted`, `completed` or `failed` operation state

All write operations require `regions:manage`; operation status uses `regions:read`. Robust validates the requested node/state, records the request in the audit trail and sends an addressed NexBus command to the selected simulator node. Placement mutations additionally perform the grid/VarRegion collision check before dispatch. The simulator rechecks safety before changing scene state.

Managed execution is deliberately disabled by default with `ManagedRegionCommands=false` in `[NexVerseNodeAgent]`. It must only be enabled when the bidirectional NexBus path is authenticated and protected by TLS or private networking. A node must advertise this capability in its heartbeat before Robust accepts a mutation for it.

Created regions receive a dedicated NexVerse-managed INI file and a UDP port from the configured managed port range. Moves and lifecycle operations are permitted only for those managed region files. Move, stop and restart are refused while root agents are present. Stop uses the normal scene-close path and therefore persists/deregisters the region without deleting its stored objects. Start reloads the existing managed INI. Restart closes and recreates the same managed region; if recreation fails, the operation reports failure and the region can remain stopped.

Create, move and lifecycle mutations accept an optional `Idempotency-Key`. For `start`, `node_id` is mandatory because a stopped region is no longer present in the live NodeAgent region projection. For `stop` and `restart`, Robust resolves the current hosting node and rejects an explicitly supplied mismatching node. Region operations are currently projected in Robust memory; the later Job Engine will provide durable long-running job history.

The API Control Center at `/api/v1/docs` now contains the first interactive read-only Grid Planner. It renders the bounded layout as a raster, supports viewport panning and display scaling, shows free/occupied/reserved/conflict states, exposes exact grid/world coordinates on hover or click, and can live-validate a selected origin with a chosen region width/height.

VarRegions are treated as multi-cell footprints. A 512x512m region, for example, occupies four 256x256m grid cells, and all of those cells are considered occupied during placement validation. The planner highlights the complete proposed footprint and reports conflicting regions.

The Grid Planner now includes the dedicated mutation and lifecycle controls. A selected free cell can be validated and handed directly to region creation with region name, simulator NodeId, Estate ID, optional explicit region UUID and optional Idempotency-Key. Credentials with `estates:read` can load the Estate catalogue into the create form so administrators can choose an Estate by name/ID; a known positive Estate ID can still be entered manually when that read scope is intentionally not granted. Robust resolves the selected Estate before queueing the create operation and rejects unknown Estate IDs before any NexBus command is sent. For moves, selecting an occupied source cell can prefill the region UUID before a destination cell is chosen. The same selected region can be started, stopped or restarted through the managed lifecycle endpoint; the node is prefilled when the live Grid/Node projection can resolve it, while a stopped region can still be started by entering its UUID and target NodeId manually. Stop and restart require an explicit browser confirmation in addition to the server-side root-agent guard.

The planner also provides global registered-region search through `GET /api/v1/regions`. Search responses expose `grid_x`, `grid_y`, `world_x` and `world_y`; choosing a result recenters the viewport and selects the exact region origin, including VarRegions. The UI follows every returned `operation_id` until `completed` or `failed` and refreshes the raster after success. Online nodes advertising `managed_region_commands=true` can be loaded into the node selector when the credential also has `simulators:read`.

Estate administration is now available in the same Control Center surface. `POST /api/v1/estates` creates an Estate, `PATCH /api/v1/estates/{estateId}` updates it, and `GET /api/v1/estates/{estateId}/management` exposes management-only lists and policies under `estates:manage`. Supported lists are managers, allowed residents, banned residents and allowed groups. Supported policies include public access, voice, direct teleport, script skipping, anonymous/minor denial and environment override. `PUT /api/v1/estates/{estateId}/regions/{regionId}` reassigns a registered region in the authoritative Estate datastore; a running region must then be restarted so its live `EstateSettings` are reloaded.

## Persistent Job Engine: Region migration

OpenGenesisLINK 0.9.3.5 adds cross-node migration for NexVerse-managed regions through the persistent Job Engine.

`POST /api/v1/jobs/regions/migrate` accepts:

```json
{
  "region_id": "00000000-0000-0000-0000-000000000000",
  "target_node_id": "simulator-002",
  "dry_run": false
}
```

The operation runs as job type `regions.migrate`. A dry-run performs the full preflight without changing the source or target. A real migration exports a verified OAR on the source, stops the source region, creates the same region UUID/Estate/grid footprint on the target, imports the OAR, verifies the target projection, and only then retires the old source managed-region configuration. Failures after source shutdown execute a best-effort rollback that stops/removes the target configuration and restarts the source.

Cross-node migration is deliberately refused unless both simulator nodes advertise the same non-empty `MigrationStorageId` from `[OpenGenesisLINKOAR]`. Administrators must use the same identifier only when `StorageRoot` is backed by the same shared filesystem/storage backend on both nodes. Matching directory names on independent local disks are not sufficient. The source region must contain no root agents, the target must be online, outside maintenance/drain state, and both nodes must have `ManagedRegionCommands=true`.

The migration verifies the SHA-256 of the exported OAR and requires the target import to report the identical archive hash. Once a migration starts, normal Job API cancellation is rejected because interruption during the cutover could split authoritative region state between nodes.

## Persistent Job Engine: Database maintenance

OpenGenesisLINK 0.9.3.5 provides bounded maintenance for the authoritative Robust `[DatabaseService]` through job type `database.maintenance`.

`POST /api/v1/jobs/database/maintenance` accepts:

```json
{
  "mode": "analyze",
  "dry_run": false
}
```

Starting this endpoint requires `admin:*`. Only the fixed `analyze` mode is accepted. The request cannot contain SQL, table names, connection strings or credentials. The worker detects the configured OpenSim storage provider and supports MySQL/MariaDB through `OpenSim.Data.MySQL.dll`, PostgreSQL through `OpenSim.Data.PGSQL.dll`, and SQLite through `OpenSim.Data.SQLite.dll`. Unsupported providers fail before any maintenance statement is executed.

The worker inventories application tables directly from provider metadata, quotes identifiers derived from that metadata, and executes the provider's native ANALYZE command one table at a time. Jobs are serialized so two database-maintenance runs cannot overlap. Cancellation is cooperative between tables and also requests cancellation of the active provider command. A partial ANALYZE run is safe: already refreshed statistics remain valid and no application rows are rewritten by OpenGenesisLINK.

`dry_run=true` connects to the configured database and inventories the tables but executes no ANALYZE statement. Job results expose only provider name, database name, counts, elapsed time and dry-run state. The configured connection string and secrets are never returned; provider error messages are redacted for password values.

OpenGenesisLINK 0.9.3.5 deliberately does not expose arbitrary SQL, `VACUUM`, `VACUUM FULL`, `OPTIMIZE TABLE`, schema migration, repair or rebuild operations through this API. Those operations can require stronger maintenance-window and backup/rollback guarantees and remain separate operational work.

## Inventory API

The 0.9.3.5 Inventory API uses the authoritative configured `IInventoryService`. Read operations require `inventory:read`; mutations require `inventory:write`. Access to another resident through `owner_id` additionally requires `admin:*`.

Implemented management operations include:

- `GET /api/v1/inventory/tree` and folder/item reads;
- `GET /api/v1/inventory/search?q=<text>&limit=<1-500>` for bounded case-insensitive search across folder names and item names/descriptions; the server scans at most 2,000 folders and 10,000 item rows per request and reports `scan.truncated` when those safety bounds are reached;
- folder create/rename/move and safe Trash handling;
- item rename/description/move, permission-aware copy, restore and safe Trash handling; Copy is refused when the source item lacks the native Copy permission, and folder move/restore operations reject ancestry cycles;
- `POST /api/v1/inventory/links` for direct item or folder links using the native link asset types;
- `POST /api/v1/inventory/items` for controlled item creation from an already-existing asset.

Raw asset-backed item creation is deliberately **administrator-only** even when the caller otherwise has `inventory:write`. The API loads the configured `AssetService`, requires the referenced asset metadata to exist, derives the asset type from that authoritative metadata, and requires explicit base/current/next-owner permission masks. This prevents a normal resident from manufacturing a full-permission inventory item merely by knowing an asset UUID. Link creation does not copy the target asset and only accepts targets already owned by the selected inventory owner.

The API never creates inventory links by cloning a linked target recursively: link-to-link chains are rejected. Folder links are inventory items with `AssetType.LinkFolder`; normal item links use `AssetType.Link`.

The OpenAPI 3.1 document publishes typed schemas for inventory folders, items, permission masks, bounded search results, asset-backed item creation and link creation so the API Control Center can render these operations without handwritten client assumptions.

## Persistent Job Engine: Inventory repair

OpenGenesisLINK 0.9.3.5 provides a conservative structural repair job for one resident inventory.

`POST /api/v1/jobs/inventory/repair` accepts:

```json
{
  "owner_id": "00000000-0000-0000-0000-000000000000",
  "dry_run": true
}
```

Starting this endpoint requires `admin:*`. The job type is `inventory.repair`; `dry_run` defaults to `true`. The worker reads the authoritative `IInventoryService` inventory root and folder skeleton, detects missing parent references, self-parenting folders and parent cycles, and reports the required repairs through normal Job Engine progress/log/result metadata.

A real repair only reattaches structurally invalid folders to the resident's authoritative inventory root. It does not delete folders or items, purge Trash, rewrite assets, or access the inventory database directly. Repairs are bounded to 1000 actions per job and are revalidated after mutation. Cancellation is cooperative between individual folder moves; a partially cancelled job leaves already-moved folders in valid root-attached locations.

This first repair worker deliberately does not attempt low-level recovery of database rows that are invisible through `IInventoryService`. Such recovery requires a separately specified datastore-maintenance contract so that MySQL/MariaDB, PostgreSQL and SQLite behavior remains explicit and testable.

Configuration:

```ini
[NexVerseWorldApi]
DatabaseMaintenanceCommandTimeoutSeconds = 300
```

## Next API work

The core user lifecycle, persistent audit history, native session revocation, OAuth/OIDC client flows, API keys and request rate limiting are connected.

The browser-facing authorization/login and consent surface, Grid Planner with global region search/jump, Estate selection and lifecycle controls, NodeAgent registry and managed region create/move/start/stop/restart execution path are now implemented. The Estate Control Plane now supports least-privilege reads plus estates:manage create/update, owner/manager management, allowed/banned resident and group lists, central access/voice/scripts policies, management-detail reads and Region→Estate reassignment. Estate deletion, templates and remaining specialized Estate policies stay separate follow-up work; MFA/passkey work remains a later identity-security extension.


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

Supported machine scopes are explicit NexVerse functional scopes such as `regions:read`, `regions:manage`, `users:read`, `inventory:read`, `estates:read`, `estates:manage`, `economy:read` and their documented write/transfer counterparts.

API-key principals use subjects in the form `api-key:<key_id>`. Disabling a key takes effect immediately because validation is performed against the persistent key store on every request.

The selectable-region endpoint now requires `regions:read` rather than `admin:*`, making it usable by restricted machine integrations while administrator bearer tokens continue to satisfy the scope through their administrative wildcard.


## OpenAPI 3.1 schema contract

The runtime OpenAPI document now publishes reusable component schemas for the primary v1 models rather than endpoint summaries only.

Current schema coverage includes:

- standardized `Error` and `Pagination`;
- `User`, `UserCreateRequest`, `UserCreateResponse` and profile/lifecycle request bodies;
- `ResidentSessionRequest` and `ResidentSessionResponse` for native resident login;
- `Region` and `RegionSearchResponse`;
- `Estate`, `EstateListResponse` and `EstateResponse` for the read-only Estate Control Plane foundation;
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


## NV$ Economy API

OpenGenesisLINK 0.9.3.6 exposes the policy-bound NV$ surface below. The authoritative ledger must be enabled through `[NexEconomy]`; otherwise economy operations return service-unavailable responses.

- `GET /api/v1/economy/balance` — read the authenticated resident balance or, for an authorized service/admin principal, `?account_id=<uuid>`; requires `economy:read`.
- `GET /api/v1/economy/virtual-account` — create/read the persistent OpenGenesisLINK-only `NVBAN` alias for the authenticated or authorized ledger account; requires `economy:read`. The returned identifier is not an IBAN or real-world bank account.
- `POST /api/v1/economy/transfers` — transfer positive integer NV$ units; requires `economy:transfer` and a mandatory `Idempotency-Key`.
- `GET /api/v1/economy/transactions/{transactionId}` — read a transaction; residents may read only transactions involving their own wallet; requires `economy:read`.
- `POST /api/v1/economy/transactions/{transactionId}/reverse` — create an append-only deterministic reversal; requires `admin:*`.
- `POST /api/v1/economy/accounts/{accountId}/status` — set `active`, `locked` or terminal `closed`; requires `admin:*`.

Resident bearer tokens may transfer only from their own account. Machine API keys with `economy:transfer` may specify `from_account_id` and are intended for trusted simulator/service integrations. A transfer retry with the same principal, `Idempotency-Key` and logical body returns the existing transaction instead of booking twice; reusing that key for conflicting transfer content is rejected.

No public API endpoint exposes raw ledger append or administrative adjustment/minting. The API uses `NexEconomyService` as its policy boundary.

### Viewer IMoneyModule adapter

Regions can enable the central adapter with:

```ini
[NexEconomyViewer]
Enabled = true
WorldApiBaseUrl = "https://world.example.invalid"
ApiKey = "${Environment|NEXVERSE_ECONOMY_API_KEY}"
FeeWalletId = ""
UploadCharge = 0
GroupCreationCharge = 0
RequestTimeoutMilliseconds = 3000
```

The API key should contain only the scopes required by the simulator, normally `economy:read` and `economy:transfer`. The region process never receives the SQL connection string and never opens the ledger database. `IMoneyModule` balance and transfer calls are translated into the World API contract.


## NV$ Banking API — Roadmap 10.3

The banking surface is hosted below `/api/v1/banking` and remains a policy adapter over `NexEconomyService`; it has no SQL or raw journal-append access.

- `GET /api/v1/banking/transactions` — bounded account transaction history.
- `GET /api/v1/banking/statements` — date-bounded statement with opening/closing balances and ledger transactions.
- `GET /api/v1/banking/reconciliation` — recompute and compare the immutable-posting balance.
- `POST /api/v1/banking/transfers` — idempotent transfer to `to_account_id` or OpenGenesisLINK-only `to_nvban`; transfer policy/limits/fees are applied centrally.
- `GET|POST /api/v1/banking/payment-requests` and `POST .../{id}/pay|cancel` — persistent payment-request lifecycle.
- `GET|PUT /api/v1/banking/accounts/{accountId}/policy` — transfer limits and optional fee wallet; writes require `admin:*`.
- `POST /api/v1/banking/escrow/{escrowId}/fund|release` — controlled escrow movement.

Generic scheduled recurring payments remain intentionally deferred by the original roadmap. Land leases provide the first persisted due-date/recurring-payment use case without introducing a background scheduler into the ledger.

## NexCommerce API — Roadmap 10.6

`/api/v1/commerce/orders` is the common order/transaction layer for vendor payments, object sales, marketplace purchases, land purchases, event tickets and rentals. Every completed order references its immutable ledger payment transaction. A merchant refund creates a ledger reversal and moves the order into `refunded`; historical payment journal rows are never edited or deleted.

Trusted simulator/service principals can create commerce orders with `economy:transfer`. Resident bearer tokens are restricted to their own buyer wallet. Refunds require a trusted service principal or administrator.

## Land Commerce API — Roadmap 10.5

The catalog/workflow API is hosted at `/api/v1/land-commerce`:

- `GET|POST /listings` — search or administratively create sale/rental listings.
- `POST /listings/{id}/deactivate` — retire a listing.
- `POST /listings/{id}/purchase` — settle a sale through NexCommerce.
- `POST /listings/{id}/lease` — create a fixed-term rental and settle its first rent period.
- `GET /leases` — account lease history.
- `POST /leases/{id}/pay` — settle a due recurring rent period.

The API catalog does not replace the simulator's parcel authority. Native parcel-for-sale flags, authorized buyer checks, abandon/deed/transfer behavior and estate/parcel permissions remain authoritative. The simulator land-buy path now refuses to call `UpdateLandSold` until the active money module confirms successful NV$ settlement.

## Firestorm / Viewer commerce — Roadmap 10.4

`NexVerseMoneyModule` remains the database-isolated `IMoneyModule` adapter. Resident payments use the World API; local object payments resolve the object owner and are recorded as NexCommerce vendor orders while preserving `OnObjectPaid`. Viewer object purchases validate current sale type/price, settle an `ObjectSale` order and then call the authoritative `IBuySellModule`; failed delivery triggers a compensating NexCommerce refund. SaleType Contents therefore uses the native contents-delivery implementation after settlement.

Local groups are provisioned on demand as central NV$ `Group` wallets through the trusted `POST /api/v1/economy/accounts/ensure` service endpoint. Native parcel purchases route their payment through NexCommerce before ownership is changed.


## NexGroups API — Roadmap 11.1

`/api/v1/groups` is the OpenGenesisLINK management/parity surface in front of the existing Groups V2 transport. The Viewer/HG connector stack is intentionally preserved until a later cut-over gate; the new API does not fork group identity or membership state.

Supported operations cover group search/create/profile/delete, members, roles and powers, role membership, invitations, notices, persistent bans, central NV$ group accounting and capability metadata. Group creation provisions a central NV$ `Group` wallet/NVBAN when the economy is enabled. Bans are persisted in `NexModerationStorePath` and are enforced inside `GroupsService.AddAgentToGroup`, so Viewer/API joins cannot bypass moderation.

Group chat continues through `GroupsMessagingModule`. Native parcel GroupID/group powers remain authoritative for group land, and native object ownership/GroupID remains authoritative for group-owned objects. The HG Groups connector remains present. NexGroups exposes stable voice policy/channel identity; actual WebRTC media transport belongs to Roadmap 13 NexVoice.

## NexExperiences API — Roadmap 11.2

`/api/v1/experiences` is the native OpenGenesisLINK Experience authority. It persists owner, optional group, admins, contributors, resident allow/block state, estate/parcel policy, script bindings, key/value data and logs.

Management/read calls use `experiences:manage` and `experiences:read`. Simulator/LSL calls use the separate `experiences:script` scope through `/api/v1/experiences/script/*`, keeping script adapters outside management authority. The simulator never opens the Experience store directly.

Script routes provide Experience resolution/details, resident permission checks, parcel/estate policy checks and asynchronous K/V operations. K/V behavior includes checked updates, missing-key update-as-create semantics, key enumeration/count and used/quota reporting.

## Experience LSL — Roadmap 11.3

The shared LSL API/YEngine now exposes the Experience permission and persistent-storage surface: `llRequestExperiencePermissions`, `experience_permissions`, `experience_permissions_denied`, `llGetExperienceDetails`, `llAgentInExperience`, CRUD K/V calls, K/V size/count/key enumeration and `llGetExperienceErrorMessage`.

Experience permission decisions and K/V data are grid-authoritative through Robust. YEngine reserves event IDs 26 and 27 for the two Experience permission events.
