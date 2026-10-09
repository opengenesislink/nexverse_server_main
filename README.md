# OpenGenesisLINK

**OpenGenesisLINK v0.9.3.8** is an independent virtual-world server platform developed by the OpenGenesisLINK project.

This repository starts from the published **OpenSimulator 0.9.3.0 ("Nessie") Release** source package. It is intentionally maintained as its own repository rather than as a GitHub fork. The long-term objective is an independent OpenGenesisLINK development line whose architecture, services, protocols, modules, tooling and branding may diverge substantially from OpenSimulator.

## Upstream baseline

- Upstream project: OpenSimulator
- Baseline release: 0.9.3.0 ("Nessie" Release)
- Release date: 8 November 2024
- Imported archive: `opensim-0.9.3.0-source.tar.gz`
- SHA-256: `8dc78639e47859e76a36a03304c1d44c905aac7807f44c1c0af671d156342eab`
- Original OpenSimulator copyright and license notices remain preserved. The primary OpenSimulator BSD license is stored at `LICENSES/BSD/OpenSimulator-0.9.3.0.txt`; contributor and third-party notices remain available in `CONTRIBUTORS.txt`, `LICENSES/` and `ThirdPartyLicenses/`.

## OpenGenesisLINK licensing

New original OpenGenesisLINK contributions are licensed under the **Mozilla Public License 2.0 (MPL-2.0)** unless a file or directory explicitly states otherwise.

Imported OpenSimulator files do **not** lose or replace their existing BSD/third-party licensing. OpenGenesisLINK changes to inherited files must preserve all applicable copyright and license notices. See `LICENSE_POLICY.md`, `NOTICE.md` and `LICENSES/BSD/README.md`.

## Versioning

The current stable release is **OpenGenesisLINK v0.9.3.8**. It was promoted after successful RC1 acceptance on 8 October 2026.

OpenGenesisLINK continues the existing 0.9.3.x development line. OpenSimulator 0.9.3.0 remains the historical source baseline; upstream OpenSimulator version numbers do not automatically become OpenGenesisLINK version numbers.

The former product name **NexVerse Server** is retired as the server-platform name. **NexVerse remains the virtual world/grid deployment built on OpenGenesisLINK.** Existing internal `NexVerse.*` namespaces, API paths and compatibility-sensitive configuration keys may remain temporarily until they can be migrated without breaking existing deployments.

Every release or development milestone that changes the product version must update `OpenSim/Framework/VersionInfo.cs` together with the corresponding release documentation.

See `VERSIONING.md` for the versioning policy.

## Roadmap

The active development roadmap is maintained in:

**[`doc/NexVerse/ROADMAP.md`](doc/NexVerse/ROADMAP.md)**

The roadmap covers the NEXJAST legacy-cleanup milestone and the World API, identity/social platform, simulator/region/estate control plane, inventory/OAR/IAR, NV$ economy and banking, Groups, Experiences, Search/Places/Land/Destination services, OGLVoice (centralized LiveKit/WebRTC voice), Pathfinding, LSL parity, modern assets/rendering, marketplace, advertising, monitoring, developer platform and the OpenGenesisLINK v0.9.4.0 consolidation target.

## Previous development milestone: OpenGenesisLINK v0.9.3.5 Dev — Inventory, OAR/IAR, Job Engine und NexBus

NexVerse 0.9.3.4 is the completed stable release of the previous product line. The **0.9.3.5 development milestone** introduced Inventory, OAR/IAR, the persistent Job Engine and NexBus; the **0.9.3.8 RC1** validation has since passed and **0.9.3.8 Stable** is released.

The 0.9.3.5 line retains viewer, Hypergrid, LSL and archive compatibility while moving operational functionality into OpenGenesisLINK-owned APIs, Job Engine workers and control-plane components. The current roadmap progress is published through the World API and kept in sync with `doc/NexVerse/ROADMAP.md` by CI.

## NexVerse 0.9.3.1 NEXJAST release foundation

The NexVerse 0.9.3.1 release contains the first NexVerse-native assemblies:

- `NexVerse.Core` — platform metadata, RBAC/scope contracts, audit contracts and NexBus foundation.
- `NexVerse.Server.Api` — Robust-hosted World API foundation.

Current World API base:

`http://world.stadt-nexverse.de/api/v1`

See `doc/NexVerse/WORLD_API.md`, `doc/NexVerse/DISPLAY_NAMES.md` and `doc/NexVerse/LEGACY_COMPONENTS.md`.

## Development model

OpenGenesisLINK is a continuation and substantial independent development based on the OpenSimulator 0.9.3.0 source baseline. Compatibility with Firestorm, Second Life protocols, Hypergrid and LSL is retained where useful, while obsolete administration and service implementations are progressively replaced by OpenGenesisLINK-owned components.

New major systems should be API-first, testable, observable and documented. Compatibility-sensitive code must be assessed before removal.

## Build baseline

The imported OpenSimulator 0.9.3.0 baseline uses the .NET 8 SDK. The original build instructions are retained in this repository.
