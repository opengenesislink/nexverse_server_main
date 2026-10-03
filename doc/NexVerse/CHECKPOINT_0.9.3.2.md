# NexVerse 0.9.3.2 Development Checkpoint

> Completed: **3 October 2026**  
> Role: **development checkpoint inside the 0.9.3.4 release train**  
> Stable release produced: **no**  
> Successor runtime line: **NexVerse 0.9.3.3 Dev**

## Scope

NexVerse 0.9.3.2 established and hardened **World API v1** as the Robust-hosted control-plane API for the current NexVerse release train.

The checkpoint is intentionally not published as a standalone stable server release. The stable line remains NexVerse 0.9.3.1 until the bounded release train reaches NexVerse 0.9.3.4.

## Completion status

Development checkpoint status: completed 3 October 2026  
Explicit roadmap checklist: 36/36 explicit checklist items completed

The 0.9.3.2 roadmap closes with **36/36 explicit checklist items completed**.

MFA and passkey support are not counted as an unfinished 0.9.3.2 item. They belong to the 0.9.3.3 identity/security milestone together with active-session and security-event work.

## Delivered World API foundation

The checkpoint includes:

- REST/JSON World API v1 and OpenAPI 3.1 contracts;
- consistent API errors, correlation IDs, pagination, filtering and sorting;
- fixed-window rate limiting and trusted-proxy controls;
- persistent idempotency protection for sensitive create/mutation operations;
- native resident access tokens and session invalidation;
- OAuth2/OIDC Authorization Code + PKCE, refresh tokens and client credentials;
- ES256 ID tokens and JWKS;
- browser login/consent flow;
- restricted machine API keys with hashed secrets and explicit scopes;
- RBAC and functional scopes;
- persistent administrative audit history;
- user provisioning and complete account lifecycle administration;
- privacy-safe aggregate statistics plus protected detailed statistics;
- self-hosted API Control Center, explorer, version history, changelog and roadmap views;
- endpoint-level audience, AI guidance and security metadata.

## Compatibility and runtime validation

The checkpoint is covered by the standard NexVerse full CI, including:

- Release build;
- successful simulator login placement;
- Firestorm-compatible LLLogin protocol smoke test;
- Hypergrid HomeAgent → Gatekeeper login;
- LSL XML-RPC RemoteData regression;
- World API runtime/OpenAPI smoke tests;
- OAuth/OIDC, API key, idempotency, audit, rate-limit and username-normalization regressions.

## Forward work retained in the release train

Some 0.9.3.4 control-plane work was implemented early while World API v1 was being hardened. It remains part of the same release train:

- NodeAgent registry and node health projection;
- Grid layout and placement validation;
- managed region create/move/start/stop/restart;
- Grid Planner search and lifecycle controls;
- Estate read API;
- Estate create/update, owner/manager and access-policy controls;
- Region-to-Estate assignment.

This work is not treated as a reason to advance into 0.9.3.5. The repository remains scope-frozen through the planned NexVerse 0.9.3.4 stable release.

## Transition

After this checkpoint:

1. runtime version advances to **NexVerse 0.9.3.3 Dev**;
2. active milestone becomes **Identity, Display Names, Profiles and Social Graph**;
3. 0.9.3.4 control-plane work already present is retained and later completed;
4. no new 0.9.3.5 implementation begins before NexVerse 0.9.3.4 is released.
