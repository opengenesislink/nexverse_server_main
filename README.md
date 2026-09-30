# NexVerse Server

NexVerse Server is the server-side platform for the NexVerse virtual world.

This repository is intended to contain the central NexVerse server codebase, including the control-plane/Robust services, simulator-side services, shared framework components, APIs, persistence integrations, and supporting tooling.

## Project status

This repository has been initialized as the main development repository for NexVerse Server.

The actual server source, build configuration, runtime configuration templates, migrations, tests, and deployment tooling can now be imported incrementally without mixing generated files, local secrets, or build output into version control.

## Planned repository areas

- `NexVerse/` — shared framework and server implementation
- `bin/` — runtime configuration templates and launch scripts
- `tests/` — automated tests and conformance tests
- `doc/` — architecture, operations, API and release documentation
- `scripts/` — build, verification, migration and deployment helpers
- `.github/` — repository automation and CI workflows

The concrete structure should follow the actual source tree when the current NexVerse Server codebase is imported.

## Development principles

- Server-authoritative world state
- Robust/control-plane and simulator separation
- API-first administration
- PostgreSQL-compatible persistence
- Explicit migration/version tracking
- No secrets or production credentials in Git
- Reproducible builds and verification
- Backwards-aware protocol development for supported viewers and integrations

## Build

Build instructions will be documented alongside the imported source tree. NexVerse Server currently targets the .NET 8 generation of the runtime.

## Configuration and secrets

Commit configuration templates only.

Do **not** commit production passwords, API keys, database credentials, certificates, voice credentials, tokens, or other secrets. Local/runtime secrets should be supplied through protected environment files or the deployment environment.

## Repository

`opengenesislink/nexverse_server_main`

---

Copyright © OpenGenesisLINK contributors.
