# OpenGenesisLINK

**OpenGenesisLINK v0.9.3.5 Dev** is an independent virtual-world server platform developed by the OpenGenesisLINK project.

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

The active development version is **OpenGenesisLINK v0.9.3.5 Dev**.

OpenGenesisLINK continues the existing 0.9.3.x development line. OpenSimulator 0.9.3.0 remains the historical source baseline; upstream OpenSimulator version numbers do not automatically become OpenGenesisLINK version numbers.

The former product name **NexVerse Server** is retired as the server-platform name. **NexVerse remains the virtual world/grid deployment built on OpenGenesisLINK.** Existing internal `NexVerse.*` namespaces, API paths and compatibility-sensitive configuration keys may remain temporarily until they can be migrated without breaking existing deployments.

Every release or development milestone that changes the product version must update `OpenSim/Framework/VersionInfo.cs` together with the corresponding release documentation.

See `VERSIONING.md` for the versioning policy.

## Roadmap

The active development roadmap is maintained in:

**[`doc/NexVerse/ROADMAP.md`](doc/NexVerse/ROADMAP.md)**

The roadmap covers the NEXJAST legacy-cleanup milestone and the planned World API, identity/social platform, simulator/region/estate control plane, inventory/OAR/IAR, NV$ economy and banking, Groups, Experiences, Search/Places/Land/Destination services, NexVoice, Pathfinding, LSL parity, modern assets/rendering, marketplace, advertising, monitoring, developer platform and the NexVerse 0.9.4.0 consolidation target.

## Active development: OpenGenesisLINK v0.9.3.5 Dev — Inventory, OAR/IAR, Job Engine und NexBus

The 0.9.3.2 and 0.9.3.3 development checkpoints were completed on 3 October 2026. The release train now advances to the bounded 0.9.3.4 Simulator/Region/Estate Control Plane scope while 0.9.3.1 remains the current stable release.

The current release train is deliberately bounded: complete 0.9.3.3 Identity/Profile/Social work, then finish 0.9.3.4 Simulator/Region/Estate Control Plane and release that line. No new 0.9.3.5 implementation work starts before the stable 0.9.3.4 release.

## NexVerse 0.9.3.1 NEXJAST release foundation

The NexVerse 0.9.3.1 release contains the first NexVerse-native assemblies:

- `NexVerse.Core` — platform metadata, RBAC/scope contracts, audit contracts and NexBus foundation.
- `NexVerse.Server.Api` — Robust-hosted World API foundation.

Current World API base:

`http://world.stadt-nexverse.de/api/v1`

See `doc/NexVerse/WORLD_API.md`, `doc/NexVerse/DISPLAY_NAMES.md` and `doc/NexVerse/LEGACY_COMPONENTS.md`.

## Development model

OpenGenesisLINK is a continuation and substantial independent development based on the OpenSimulator 0.9.3.0 source baseline. Compatibility with Firestorm, Second Life protocols, Hypergrid and LSL is retained where useful, while obsolete administration and service implementations are progressively replaced by NexVerse-native components.

New major systems should be API-first, testable, observable and documented. Compatibility-sensitive code must be assessed before removal.

## Build baseline

The imported OpenSimulator 0.9.3.0 baseline uses the .NET 8 SDK. The original build instructions are retained in this repository.
