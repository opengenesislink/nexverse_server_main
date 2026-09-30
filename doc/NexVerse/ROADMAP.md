# NexVerse Server Roadmap

> Status: Active development roadmap  
> Current development line: **NexVerse 0.9.3.1 Dev**  
> Current milestone codename: **NEXJAST**  
> Historical source baseline: OpenSimulator 0.9.3.0 ("Nessie")

## 1. Vision

NexVerse Server is being developed from the OpenSimulator 0.9.3.0 source baseline into an independent virtual-world platform.

The long-term objective is not to preserve every historical OpenSimulator subsystem. NexVerse keeps protocol and viewer compatibility where it is useful, while legacy administration, voice, web, monitoring and service components are replaced by modern NexVerse-native implementations.

The target architecture is centered around:

- a modern authenticated World API;
- a clean Robust-based control plane;
- managed simulator and region nodes;
- Firestorm/Second Life protocol compatibility where required;
- full-featured NV$ economy and banking;
- modern identity, social, profile, inventory, group and estate services;
- Experiences and Pathfinding;
- a native WebRTC/Janus voice platform;
- a systematic LSL parity program;
- search, places, land and destination services;
- strong monitoring, auditing and operational tooling;
- a documented extension and developer platform.
- a defined simulator runtime profile using ubODE physics, ubODEMeshmerizer and Warp3D-generated map tiles.

## 2. Development principles

### 2.1 NexVerse Clean Core

New NexVerse functionality should be implemented in NexVerse-owned modules and namespaces instead of extending obsolete OpenSimulator subsystems indefinitely.

A legacy component may remain only when at least one of the following is true:

1. Firestorm or Second Life protocol compatibility requires it.
2. Hypergrid compatibility requires it.
3. LSL compatibility requires it.
4. Replacing it would currently create more risk than technical benefit.

When a NexVerse replacement reaches functional parity, the replaced legacy implementation should be removed from source, build files, configuration and documentation.

### 2.2 Compatibility before deletion

Legacy code is not removed solely because it is old.

Before deletion, every candidate must be checked for:

- Firestorm viewer dependencies;
- LLUDP/CAPS dependencies;
- LSL functions, events and constants;
- Hypergrid dependencies;
- simulator-to-Robust dependencies;
- database migrations and persisted data;
- OAR/IAR compatibility.

Example: XML-RPC RemoteAdmin is obsolete and should be removed. XML-RPC functionality used by LSL RemoteData must remain in a compatibility layer until the relevant LSL behavior is intentionally replaced.

### 2.3 API-first

New management functions must be exposed through the authenticated NexVerse World API rather than introducing new private ad-hoc RPC mechanisms.

Primary API host:

`world.stadt-nexverse.de`

Target API root:

`/api/v1/`

### 2.4 Event-driven platform

Cross-service state changes should emit structured events through NexBus.

Examples:

- `avatar.logged_in`
- `avatar.logged_out`
- `friend.added`
- `inventory.changed`
- `region.started`
- `region.stopped`
- `economy.transaction.completed`
- `experience.permission.granted`
- `voice.session.joined`
- `object.message.received`

### 2.5 Every feature is operational

A feature is not considered complete merely because the happy path works.

Where applicable, completion requires:

- API contract;
- authentication and authorization;
- tests;
- Firestorm compatibility verification;
- LSL compatibility verification;
- migrations;
- metrics;
- structured logging;
- audit logging;
- documentation;
- upgrade and rollback considerations.

## 3. Versioning policy

NexVerse keeps its own version sequence independently of the OpenSimulator upstream version.

The historical source baseline remains OpenSimulator 0.9.3.0 regardless of future NexVerse versions.

Development progression:

- development: `X.Y.Z.N Dev`
- release candidate: `X.Y.Z.N RC1`, `RC2`, ...
- release: `X.Y.Z.N`

Each development milestone increments the NexVerse product version.

The first active NexVerse development milestone is:

**NexVerse 0.9.3.1 Dev — NEXJAST**

A larger architectural platform generation will advance to **NexVerse 0.9.4.0**.

## 4. Compatibility levels

The product version should not be overloaded to represent every protocol revision. NexVerse should maintain explicit compatibility levels.

Planned examples:

- NexVerse Server Version
- NexVerse API Version
- NexVerse Internal Protocol Version
- LSL Compatibility Level
- Firestorm Compatibility Profile
- Hypergrid Compatibility Level
- NexVoice Protocol Version
- NV$ Economy Protocol Version
- NexBus Event Schema Version

These values can later be exposed through GridInfo and the World API.

---

# Milestone 0.9.3.1 Dev — NEXJAST

## 5. Legacy Cleanup and Platform Foundation

NEXJAST establishes a clean development foundation before the new API and service architecture expand.

### 5.1 Remove RemoteAdmin

The historical XML-RPC RemoteAdmin system is to be removed completely.

Scope:

- remove RemoteAdmin application plugin;
- remove RemoteAdmin configuration sections;
- remove RemoteAdmin passwords and settings;
- remove RemoteAdmin build/project entries;
- remove RemoteAdmin documentation and examples;
- remove dependencies that exist only for RemoteAdmin;
- replace administrative use cases with the NexVerse World API.

Administrative control must no longer depend on the old RemoteAdmin interface.

### 5.2 Remove legacy voice systems

Remove legacy Vivox and FreeSwitch implementations that will be replaced by NexVoice.

Target removals include:

- VivoxVoiceModule;
- FreeSwitchVoiceModule;
- FreeSwitch service;
- FreeSwitch connectors;
- FreeSwitch handlers;
- FreeSwitch interfaces;
- related Robust connectors;
- obsolete configuration sections;
- build and solution entries.

NexVoice becomes the only long-term NexVerse voice architecture.

### 5.3 Remove obsolete IRC bridge

Remove the legacy IRC bridge and related connector/state classes unless a concrete compatibility dependency is discovered.

### 5.4 Legacy OpenID service

The old OpenID provider/server implementation has been removed from Robust.

The optional legacy LLLogin response fields remain temporarily as a compatibility reserve until Firestorm runtime validation is complete. Modern NexVerse identity will use the NexVerse authentication architecture instead.

### 5.5 Repository hygiene

Clean generated build state from source control.

Remove tracked artifacts such as:

- `obj/`
- NuGet generated caches;
- generated MSBuild editor configuration;
- absolute file lists;
- generated intermediate build files.

The root runtime `bin/` directory must be handled carefully because the inherited OpenSimulator layout stores required runtime configuration there.

### 5.6 Legacy component inventory

Create and maintain a migration table classifying inherited components as:

- KEEP — required and healthy;
- COMPAT — retained only for protocol/viewer/LSL/HG compatibility;
- REPLACE — scheduled for NexVerse replacement;
- REMOVE — no longer required.

### 5.7 NexVerse native foundation status

Implemented during NEXJAST:

- [x] `NexVerse.Core` native assembly introduced.
- [x] RBAC/scope contracts introduced.
- [x] audit event contract introduced.
- [x] in-memory NexBus foundation introduced.
- [x] `NexVerse.Server.Api` native assembly introduced.
- [x] Robust loads the World API connector on the public listener.
- [x] `/api/v1`, `/health`, `/version`, `/capabilities` and `/openapi.json` implemented.
- [x] World API correlation IDs, API version headers, audit events and NexBus request events implemented.
- [x] interim AuthenticationService bearer-token validation connected (privileged routes remain disabled by default on plaintext HTTP).
- [x] user API phase 1 connected: self/account lookup, admin search, account creation/provisioning, account profile update, password set/reset and UserLevel update.
- [x] account creation requires an explicit selectable Home/Start region and initializes both Home and initial Last/Start position.
- [x] complete current OSSL function surface enabled through an explicit NexVerse permission profile.
- [x] CI rejects newly added threat-checked OSSL functions without an explicit enabled policy rule.
- [x] Hypergrid grid-identity URI normalization handles default ports, trailing slashes and host casing consistently.
- [ ] NexVerse-native OIDC/scoped token issuance fully replaces the interim authentication bridge.
- [x] native HMAC-signed scoped access-token foundation and legacy-token exchange implemented; native tokens are lifecycle-stamp bound.
- [x] persistent OAuth2/OIDC core implemented: Authorization Code + PKCE S256, refresh-token rotation/revocation, ES256 ID tokens/JWKS, persistent client registration and service-account client_credentials.
- [x] password, account-state and explicit session revocation invalidate resident access/refresh sessions.
- [x] complete user lifecycle API connected: create, update, soft-delete/deactivate, lock/unlock, ban/unban and password workflow.
- [x] account lifecycle state is persisted independently of UserLevel and enforced by Viewer login and World API authentication.
- [x] account creation is fail-closed through provisioning/provisioning_failed states so incomplete accounts cannot log in.
- [x] distributed NexBus transport connected end-to-end between Robust and simulator nodes.
- [x] distributed NexBus core plus authenticated HTTP/HMAC peer transport foundation implemented with relay-loop deduplication and bounded outbound queue.
- [x] NexVerse NodeAgent region module publishes node/region lifecycle and health heartbeats and exposes local/inbound NexBus pub/sub to simulator modules.
- [ ] production metrics/OpenTelemetry implemented end-to-end.
- [x] NexMetrics Prometheus-compatible registry/export endpoint and OpenTelemetry-compatible ActivitySource foundation implemented; external collector/export pipeline remains pending.

### 5.8 NEXJAST definition of done

- [x] RemoteAdmin removed from code/build/config.
- [x] Vivox removed from code/build/config.
- [x] FreeSwitch removed from code/build/config.
- [x] obsolete IRC bridge removed.
- [x] generated `obj/` build artifacts removed from source control.
- [x] simulator builds successfully (validated by NEXJAST CI Release build).
- [x] Robust builds successfully (validated by NEXJAST CI Release build).
- [x] World API runtime smoke test passes against a started Robust process.
- [x] OSSL policy coverage is regression-checked by NEXJAST CI.
- [x] HG local-grid URI identity normalization is regression-tested by NEXJAST CI.
- [ ] HG login remains functional.
- [ ] local grid login remains functional through successful simulator placement.
- [x] Firestorm-compatible LLLogin XML-RPC endpoint is runtime smoke-tested with a real Robust process.
- [ ] Firestorm baseline smoke tests pass through successful viewer/simulator login.
- [x] core LSL XML-RPC RemoteData channel lifecycle and invalid-channel handler behavior are runtime-regression tested against the built XMLRPCModule.
- [x] NexVerse simulator profile actively enables the local XmlRpcRouterModule and RemoteData listener on port 20800; deployments with multiple simulator processes on one host must override the port per process.
- [ ] end-to-end LSL XML-RPC RemoteData callback with a running region/script remains to be runtime verified.
- [x] development runtime version source identifies itself as NexVerse 0.9.3.1 Dev.

---

# Milestone 0.9.3.2

## 6. NexVerse World API v1

Introduce the modern authenticated API hosted by Robust and exposed through:

`world.stadt-nexverse.de/api/v1/`

### 6.1 API architecture

- REST/JSON API;
- OpenAPI 3.1 specification;
- versioned endpoints;
- consistent error model;
- pagination;
- filtering and sorting;
- idempotency where required;
- correlation/request IDs;
- rate limiting;
- structured audit events.

### 6.2 Authentication and authorization

- OAuth2/OIDC-style flows where appropriate;
- signed access tokens;
- service accounts;
- API keys for restricted machine use;
- scopes;
- RBAC;
- future MFA/passkey integration.

Example scopes:

- `users:read`
- `users:write`
- `inventory:read`
- `inventory:write`
- `friends:manage`
- `regions:restart`
- `estates:manage`
- `economy:transfer`
- `admin:*`

### 6.3 User administration

API coverage:

- create account;
- read account;
- edit account;
- soft-delete/deactivate account;
- lock/unlock account;
- ban/unban account;
- password/admin reset flows;
- change UserLevel;
- account search;
- account history;
- account audit records.

### 6.4 API web interface

Provide a modern web interface under `world.stadt-nexverse.de`.

It should include:

- searchable endpoint catalogue;
- request/response schemas;
- live API explorer;
- authentication requirements;
- permission scopes;
- example requests;
- error documentation;
- version history;
- deprecation notices.

### 6.5 Citizen/Admin AI instructions

OpenAPI metadata should distinguish citizen-facing and admin-facing usage.

Planned custom metadata:

`x-nexverse-audience`

`x-nexverse-ai-instruction`

Each endpoint can document:

- citizen portal purpose;
- administrator purpose;
- allowed roles;
- ChatGPT/API-agent usage guidance;
- security constraints.

---

# Milestone 0.9.3.3

## 7. Identity, Display Names, Profiles and Social Graph

### 7.1 Resident-compatible usernames

Support canonical internal usernames such as:

- `antonia.resident`
- `jam.resident`

Allow login with the short first-name form when the implied last name is `Resident`.

Examples:

- `Jam` resolves to `jam.resident`
- `jam.resident` remains valid

The `Resident` suffix should not have to be displayed inworld for normal resident accounts.

### 7.2 Display Names

Implement full Display Name support throughout:

- login response;
- viewer name cache;
- nearby avatars;
- profiles;
- chat;
- IM;
- groups;
- objects/creator views where appropriate;
- search;
- web profiles.

### 7.3 WebProfileV3

Create a NexVerse-native profile service supporting:

- profile image;
- display name;
- username;
- about text;
- interests;
- picks;
- classifieds;
- profile visibility;
- online visibility;
- partner relationship;
- groups visibility;
- search visibility;
- privacy controls.

### 7.4 Friends and relationships API

Complete API management for:

- friendship request;
- accept/decline;
- remove friend;
- rights;
- online status;
- map/location rights;
- object-edit rights where supported;
- block/mute;
- partner relationship;
- relationship history and audit.

### 7.5 Security extensions

Prepare:

- TOTP MFA;
- passkeys/WebAuthn;
- active sessions;
- device/session revocation;
- API token management;
- login history;
- security events.

---

# Milestone 0.9.3.4

## 8. Simulator, Region and Estate Control Plane

### 8.1 NexVerse NodeAgent

Introduce an authenticated simulator-side control agent.

Every simulator node registers with Robust and publishes:

- node ID;
- hostname;
- version;
- capabilities;
- uptime;
- CPU;
- RAM;
- disk;
- region list;
- agent count;
- health state;
- heartbeat timestamp.

### 8.2 Simulator management

World API functions:

- list simulators;
- inspect health;
- start/stop/restart managed simulator services;
- retrieve logs;
- execute approved administrative commands;
- maintenance mode;
- drain users;
- update/roll back node.

### 8.3 Region management

- create region;
- update region configuration;
- enable/disable region;
- start/stop/restart region;
- migrate region between nodes;
- change region placement;
- set region type;
- view region metrics;
- region health;
- access lists;
- ban lists;
- environment;
- parcel summary;
- region console actions via controlled APIs.

### 8.4 Interactive World Grid Planner

Add an administrator-facing raster world map to the NexVerse World API web interface for region placement and capacity planning.

The planner must provide:

- a scrollable and zoomable grid based on the OpenSim 256m base region cell;
- mouse hover over every raster cell showing exact grid coordinates;
- display of both region-grid coordinates and absolute world-meter coordinates;
- clear visual distinction between free, occupied, reserved and unavailable cells;
- region name, UUID, simulator/node, size and status for occupied cells;
- correct multi-cell occupancy for VarRegions larger than 256x256m;
- detection of overlap before a new region is created;
- click-to-select a free cell;
- direct hand-off of the selected coordinates into the create-region form;
- optional region-size selection with live preview of the footprint before creation;
- search/jump to region name, UUID or coordinates;
- pan/zoom controls and useful viewport bounds;
- filtering by simulator node, estate, region state and region type;
- refresh after region create, delete, move, start or stop operations.

The raster must be driven by API data rather than by scraping rendered map tiles.

Planned API contracts:

- `GET /api/v1/grid/layout` — return region placements and occupancy for a bounded grid window;
- `GET /api/v1/grid/cells/{x}/{y}` — inspect one grid cell and any occupying region;
- `GET /api/v1/grid/validate-placement` — validate whether a proposed origin and region size are free;
- `POST /api/v1/regions` — create a region using validated grid coordinates;
- `PATCH /api/v1/regions/{regionId}/placement` — move a region after safety checks.

The layout response should expose enough information for the UI to calculate occupancy without ambiguity:

- origin grid X/Y;
- origin world X/Y in meters;
- region width/height in meters;
- width/height in 256m cells;
- occupied cell range;
- region UUID/name;
- simulator/node ID;
- online/offline state;
- estate ID where available.

Placement validation must reject overlaps, including partial overlap with VarRegions.

### 8.5 Estate management

Complete Estate API:

- estate create/update/delete;
- owner;
- managers;
- allowed residents;
- banned residents;
- allowed groups;
- estate access;
- voice policy;
- scripts policy;
- maturity;
- terrain/environment defaults;
- experience rules;
- region membership;
- estate templates.

---

# Milestone 0.9.3.5

## 9. Inventory, OAR/IAR, Job Engine and NexBus

### 9.1 Complete Inventory API

Support:

- browse inventory tree;
- create folder;
- rename folder;
- move folder;
- delete folder;
- restore from trash;
- empty trash;
- create item;
- rename item;
- move item;
- copy item;
- delete item;
- sort;
- search;
- inspect permissions;
- update allowed metadata;
- links;
- Lost & Found;
- asset reference information.

### 9.2 OAR management

API-driven region archives:

- export OAR;
- import OAR;
- validate archive;
- dry-run inspection;
- progress;
- failure reporting;
- archive metadata;
- storage policy.

### 9.3 IAR management

API-driven avatar inventory archives:

- export IAR;
- import IAR;
- partial subtree export;
- conflict policy;
- progress;
- failure reporting;
- encrypted backup option.

### 9.4 NexVerse Job Engine

Long-running operations become jobs rather than blocking HTTP calls.

Examples:

- OAR import/export;
- IAR import/export;
- inventory repair;
- region migration;
- backup;
- restore;
- asset reindex;
- database maintenance.

Job API should expose:

- queued;
- running;
- completed;
- failed;
- cancelled;
- progress;
- logs;
- result metadata.

### 9.5 NexBus

Create the internal event/message bus for:

- simulator events;
- object messages;
- economy events;
- identity events;
- social events;
- group events;
- voice presence;
- experiences;
- monitoring.

Implemented transport foundation:

- local in-memory pub/sub remains the fallback;
- distributed bus preserves event IDs/timestamps across processes;
- relay-loop suppression uses bounded EventId deduplication;
- HTTP peer fan-out is HMAC-SHA256 authenticated;
- outbound delivery is queued so API publishers do not block on peer latency;
- transport is configuration-driven and disabled by default;
- simulator integration is provided by `NexVerse.RegionModules.dll` / `NexVerseNodeAgentModule`, including HMAC inbound/outbound transport, NodeAgent heartbeats and region lifecycle events.

### 9.6 Cross-region object-to-object communication

Provide a supported mechanism for objects in different regions and different simulators to communicate through NexBus while retaining permission, rate-limit and abuse controls.

---

# Milestone 0.9.3.6

## 10. NV$ Economy, Banking, Commerce and Land

### 10.1 Double-entry ledger

Build the economy around immutable double-entry accounting.

Account classes:

- resident;
- group;
- business/merchant;
- estate;
- object/merchant endpoint;
- system;
- escrow.

### 10.2 Virtual NexVerse bank accounts

Provide fictional virtual account identifiers/IBAN-style numbers for NexVerse-only use.

These identifiers must never be represented as real-world bank accounts.

### 10.3 Banking functions

- balances;
- transaction history;
- statements;
- transfers;
- payment requests;
- refunds;
- escrow;
- limits;
- fees;
- recurring payments later;
- account locking;
- reconciliation;
- audit trails.

### 10.4 Viewer economy compatibility

Support Firestorm-visible flows including:

- balance;
- pay resident;
- pay object;
- buy object;
- buy contents;
- land purchase;
- group accounting where applicable;
- transaction notifications.

### 10.5 Land commerce

- parcel for sale;
- buy land;
- abandon land;
- transfer land;
- rent/lease model;
- recurring rent;
- land listing;
- land search;
- purchase history;
- estate restrictions.

### 10.6 NexCommerce

Unify:

- inworld vendor payments;
- object sales;
- marketplace purchases;
- land purchases;
- event tickets;
- rentals;
- merchant refunds.

---

# Milestone 0.9.3.7

## 11. NexGroups and NexExperiences

### 11.1 NexGroups

Replace the historical group stack only after parity is reached.

Target scope:

- create/delete group;
- profile;
- members;
- roles;
- role powers;
- owner;
- invitations;
- bans;
- notices;
- group chat;
- group land;
- group accounting;
- group search;
- group voice;
- group permissions;
- group-owned objects;
- HG behavior where supported.

### 11.2 Experiences

Implement Experiences as a native NexVerse service.

Core model:

- owner;
- group;
- admins;
- contributors;
- allowed residents;
- blocked residents;
- estate policy;
- parcel policy;
- permissions;
- persistent key/value storage;
- logs;
- audit events.

### 11.3 Experience LSL

Implement and verify relevant Experience LSL functions/events, including permission flows and persistent storage behavior.

---

# Milestone 0.9.3.8

## 12. Search, Places, Land and Destination Guide

Create NexVerse-native discovery services used by both web and viewer.

### 12.1 NexSearch

Index and search:

- people;
- groups;
- regions;
- parcels;
- places;
- events;
- land for sale;
- land for rent;
- classifieds;
- experiences;
- destinations.

### 12.2 Places

Create web place pages containing:

- region/place name;
- description;
- maturity;
- images;
- owner;
- coordinates;
- teleport link;
- parcel details;
- traffic;
- tags;
- events;
- related destinations.

### 12.3 Land portal

Provide web/API views similar in purpose to modern virtual-world land marketplaces:

- owned land;
- land for sale;
- rentals;
- residential;
- commercial;
- featured listings;
- price;
- area;
- maturity;
- region type;
- map location.

### 12.4 Destination Guide

The StadtPortal manages destination content that is also visible in compatible viewers.

Features:

- categories;
- subcategories;
- featured destinations;
- editor picks;
- recently added;
- popular/hot;
- events;
- maturity filtering;
- curated collections;
- teleport links;
- submission and moderation workflow.

---

# Milestone 0.9.3.9

## 13. NexVoice

Build an independent WebRTC voice platform designed for NexVerse and optional external-grid use.

Primary service:

`voice.stadt-nexverse.de`

Architecture:

- NexVoice control service;
- WebRTC;
- Janus;
- STUN/TURN via Coturn;
- secure short-lived credentials;
- spatial voice;
- parcel voice;
- region voice;
- group voice;
- direct calls;
- conferences;
- moderation;
- external-grid tenant support.

### 13.1 Viewer behavior

- speaker indicator visible for every speaking avatar;
- speaking orb above avatars;
- spatial position updates;
- mute;
- volume;
- moderation;
- parcel/region channel transitions.

### 13.2 External grid service

Support isolated tenants for third-party grids with:

- grid registration;
- API credentials;
- domain configuration;
- quotas;
- billing plans later;
- abuse controls;
- metrics.

---

# Milestone 0.9.3.10

## 14. Pathfinding

Implement a complete pathfinding subsystem rather than a partial NPC workaround.

Scope:

- navigation mesh;
- NavMesh build/rebuild;
- dirty state;
- static obstacles;
- walkable surfaces;
- terrain;
- path costs;
- linksets;
- pathfinding characters;
- movement controller;
- viewer capabilities;
- visualization/debug data.

### 14.1 Pathfinding LSL

Target compatibility for pathfinding functions and events including the family around:

- character creation/deletion;
- navigate;
- pursue;
- evade;
- flee;
- wander;
- patrol;
- static path queries;
- closest nav point;
- character updates;
- `path_update` event.

### 14.2 Cross-region behavior

Investigate and implement safe cross-region navigation where the protocol and world topology permit it.

---

# Milestone 0.9.3.11

## 15. LSL Parity I — Core Conformance

LSL compatibility is a continuous program and begins before this milestone. This milestone is the first concentrated parity wave.

### 15.1 Machine-readable LSL matrix

Track every relevant:

- function;
- event;
- constant;
- status;
- implementation location;
- support level;
- forced delay;
- permissions;
- test coverage;
- known incompatibilities.

Support levels:

- FULL;
- PARTIAL;
- STUB;
- MISSING;
- INTENTIONALLY UNSUPPORTED.

### 15.2 Conformance tests

Create automated tests for:

- signature;
- return type;
- parameters;
- constants;
- events;
- permissions;
- timing/delay;
- errors;
- asynchronous callbacks;
- dataserver responses;
- script state;
- region crossing;
- edge cases.

### 15.3 LSL test region

Create a dedicated automated NexVerse LSL conformance region capable of running a large scripted regression suite.

### 15.4 Script sandbox hardening

Measure and limit:

- CPU;
- memory;
- event queue;
- HTTP requests;
- timers;
- script execution time;
- abusive event generation.

---

# Milestone 0.9.3.12

## 16. LSL Parity II — Advanced Systems

Concentrate on modern and complex LSL domains:

- Experiences;
- Pathfinding;
- HTTP;
- JSON;
- media;
- parcel/land;
- inventory;
- attachments;
- animations;
- camera;
- object details;
- environment;
- economy;
- remote data compatibility;
- newer server-side LSL additions.

### 16.1 LSL profiler and developer tools

Provide API/web tooling for:

- script CPU;
- memory;
- event queue;
- event rate;
- HTTP calls;
- error logs;
- execution timing;
- object/script location.

---

# Milestone 0.9.3.13

## 17. Assets, Rendering, Marketplace and Media

### 17.1 NexAsset

Modernize asset management beyond basic FSAssets operation.

Long-term capabilities:

- content hashes;
- integrity verification;
- deduplication;
- metadata;
- thumbnails;
- garbage collection;
- optional object storage;
- optional S3-compatible backend;
- optional CDN integration.

FSAssets remains supported during migration.

### 17.2 Modern material/content pipeline

Add or improve support for:

- glTF;
- PBR materials;
- metallic/roughness;
- emissive materials;
- modern terrain materials;
- reflection/environment data;
- modern upload validation.

Legacy Collada remains available for compatibility while glTF becomes the preferred future direction.

### 17.3 NexMarketplace

Build a marketplace integrated with NV$:

- merchants;
- stores;
- listings;
- products;
- variants;
- delivery;
- redelivery;
- refunds;
- search;
- merchant analytics;
- vendor API;
- inventory delivery integration.

### 17.4 NexMedia

Modernize parcel/shared media controls with explicit security and permission policies.

---

# Milestone 0.9.3.14

## 18. NexAds, Events and Notifications

### 18.1 NexAds

Create a provider-independent inworld advertising platform.

Features:

- advertiser account;
- campaign;
- creative;
- billboard placement;
- parcel targeting;
- region targeting;
- scheduling;
- impressions;
- clicks/interactions;
- budget;
- NV$ billing;
- moderation;
- reporting.

External advertising providers may be integrated through policy-compliant adapters. NexVerse should not depend on standard Google AdSense code being embedded directly into arbitrary inworld surfaces.

### 18.2 Events

- create/manage event;
- location;
- schedule;
- organizer;
- maturity;
- event image;
- RSVP;
- admission price;
- NV$ ticketing later;
- destination integration;
- search integration.

### 18.3 Notification Center

Unify notifications for:

- friends;
- groups;
- purchases;
- payments;
- events;
- estates;
- inventory delivery;
- moderation;
- voice;
- system status.

Provide real-time delivery through WebSocket/SSE and optional future mobile push.

---

# Milestone 0.9.3.15

## 19. Monitoring, Security and Operations

### 19.1 NexMetrics

Metrics should cover at least:

- Robust health;
- simulator health;
- region health;
- region FPS;
- physics FPS;
- script time;
- agent count;
- asset latency;
- database latency;
- API latency;
- queue lengths;
- failed login;
- failed teleport;
- voice sessions;
- economy operations;
- disk/storage usage.

### 19.2 Observability

- Prometheus-compatible metrics;
- OpenTelemetry;
- distributed tracing;
- structured logs;
- correlation IDs;
- dashboards;
- alerting.

### 19.3 Security

- RBAC enforcement;
- MFA/passkeys;
- API rate limiting;
- secret management;
- secure service credentials;
- audit logs;
- abuse controls;
- dependency scanning;
- secure defaults.

### 19.4 Privacy and account data

Provide:

- account data export;
- account deletion/anonymization workflow;
- session review;
- token revocation;
- privacy configuration;
- audit trail.

### 19.5 Moderation

NexModeration should support:

- abuse reports;
- evidence references;
- avatar freeze/eject;
- estate ban;
- grid ban;
- content takedown;
- moderation notes;
- appeal workflow;
- moderation audit history.

### 19.6 Backup and disaster recovery

- MariaDB backup;
- asset backup;
- OAR snapshots;
- IAR backup;
- encrypted backup targets;
- restore testing;
- point-in-time strategy where practical.

### 19.7 Rolling updates

- node drain;
- region restart orchestration;
- rolling simulator upgrade;
- health gate;
- rollback;
- version compatibility check.

---

# Milestone 0.9.3.16

## 20. Developer Platform, SDKs and Extensibility

### 20.1 Developer portal

Under the World API web interface:

- API applications;
- API keys;
- OAuth clients;
- Webhooks;
- request logs;
- OpenAPI documentation;
- examples;
- sandbox/test credentials where safe.

### 20.2 Official SDKs

Initial target SDKs:

- C#/.NET;
- TypeScript/JavaScript;
- Python.

### 20.3 Webhooks and realtime APIs

Provide event delivery for selected NexBus events through:

- Webhooks;
- WebSocket;
- Server-Sent Events.

### 20.4 NexVerse module API

Create a stable extension interface for future modules with:

- dependency injection;
- lifecycle;
- configuration;
- capabilities;
- health;
- metrics;
- permissions;
- event subscriptions.

New modules should not require patching unrelated core code.

### 20.5 Feature negotiation

Expose supported server capabilities so compatible viewers and tools can adapt without guessing by product version.

Examples:

- Experiences;
- Pathfinding;
- PBR;
- NexVoice protocol level;
- NV$ protocol level;
- LSL compatibility level.

---

# Milestone 0.9.4.0

## 21. NexVerse Platform Generation 1

NexVerse 0.9.4.0 is the consolidation milestone.

It should only be declared when the platform behaves as a coherent NexVerse system rather than as a collection of additions to the OpenSimulator baseline.

Target characteristics:

- World API is the primary administration interface.
- RemoteAdmin no longer exists.
- legacy Vivox/FreeSwitch voice code no longer exists.
- NexVoice is production-capable.
- user/social/profile management is API-first.
- simulator/region/estate management is API-first.
- NV$ economy and banking are stable and auditable.
- Firestorm land purchase/payment flows work.
- inventory API and OAR/IAR jobs are stable.
- NexGroups is production-capable.
- Experiences are available.
- Pathfinding is available.
- LSL parity is measurable and regression-tested.
- Search/Places/Land/Destination services are integrated.
- monitoring and audit coverage are production-grade.
- NexBus and Job Engine are core infrastructure.
- upgrade, backup and rollback procedures exist.

---

# 22. Continuous workstreams

The following workstreams span multiple milestones and must not be postponed until one late release.

## 22.1 Simulator runtime profile

The NexVerse simulator baseline is intentionally standardized to:

- `physics = ubODE`;
- `meshing = ubODEMeshmerizer`;
- `MapImageModule = Warp3DImageModule`;
- generated map tiles with terrain, prim textures and mesh rendering enabled;
- an ubODE-safe Linux stack limit in `bin/opensim.sh`.

NEXJAST CI verifies this profile and the required native ubODE libraries so accidental fallback to BulletSim, the generic Meshmerizer or the legacy map image module is treated as a regression.

## 22.2 Firestorm compatibility

Continuously test:

- login;
- teleport;
- inventory;
- profile;
- display names;
- friends;
- groups;
- IM;
- economy;
- land;
- map;
- search;
- Experiences;
- Pathfinding;
- Voice;
- uploads.

## 22.3 LSL parity

Every new NexVerse subsystem that has LSL surface area must add its LSL behavior and regression tests during implementation.

## 22.4 Hypergrid compatibility

Retain HG compatibility unless an explicitly versioned NexVerse federation mechanism replaces or supplements a particular path.

## 22.5 Database migrations

Every schema change must be versioned and upgrade-safe.

## 22.6 Documentation

Every public API and administrator-visible configuration option must be documented.

## 22.7 Security review

High-risk areas such as economy, inventory, asset access, script execution, remote administration and voice credentials require explicit security review.

---

# 23. Future research tracks

These are not committed release requirements yet, but should remain in architecture planning.

## 23.1 NexFederation

A modern signed federation layer for trusted NexVerse grids alongside Hypergrid compatibility.

Potential components:

- signed grid identities;
- service discovery;
- trust policies;
- mTLS/service authentication;
- version negotiation.

## 23.2 NexNPC

Modern NPC service with:

- profiles;
- appearance;
- inventory;
- animations;
- Pathfinding;
- scripted/API control.

AI-driven behavior may later be added as an optional module, not as a core dependency.

## 23.3 Mobile readiness

Keep social, inventory, profile, places, notifications and chat APIs usable by a future mobile client without requiring the 3D simulator protocol for ordinary account functions.

## 23.4 Region migration and seamless crossings

Improve:

- vehicle crossings;
- attachments;
- script state;
- sitting avatars;
- object crossings;
- region migration between simulator nodes.

## 23.5 Transport systems

Potential APIs for:

- landmarks;
- NexVerse URLs;
- routing;
- public transport;
- scheduled vehicles;
- destination navigation.

---

# 24. Architecture target

```text
                          NexVerse Platform
                                 |
                   world.stadt-nexverse.de
                                 |
                        NexVerse World API
                                 |
          +----------------------+----------------------+
          |                      |                      |
      Identity                NexBus                Job Engine
          |                      |                      |
   Profiles/Friends       Events/Messaging       OAR/IAR/Backup
          |                      |                      |
          +----------------------+----------------------+
                                 |
                         NexVerse Services
                                 |
      +------------+-------------+-------------+------------+
      |            |             |             |            |
   Economy       Groups      Inventory     Experiences     Search
      |            |             |             |            |
   NV$ Bank       Chat        Assets/KV      Permissions   Places
      |                                                        |
      +--------------------------+-----------------------------+
                                 |
                         NexVerse Simulator
                                 |
              +------------------+------------------+
              |                  |                  |
          Pathfinding         NexVoice        Compatibility
              |                  |                  |
            NavMesh        WebRTC/Janus    Firestorm/HG/LSL
```

---

# 25. Roadmap governance

This roadmap is a living technical planning document.

Rules for changes:

1. Do not silently remove major committed goals.
2. Record meaningful scope changes in Git history.
3. Increment the NexVerse development version when a new milestone begins.
4. Keep historical baseline/licensing references unchanged.
5. Prefer measurable completion criteria over vague "implemented" labels.
6. Compatibility regressions must be treated as defects unless intentionally documented.
7. Production releases require build, migration and smoke-test verification.
