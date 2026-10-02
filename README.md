# NexVerse Server

NexVerse Server is an independent virtual-world server platform developed by OpenGenesisLink.

This repository starts from the published **OpenSimulator 0.9.3.0 ("Nessie") Release** source package. It is intentionally maintained as its own repository rather than as a GitHub fork. The long-term objective is an independent NexVerse development line whose architecture, services, protocols, modules, tooling and branding may diverge substantially from OpenSimulator.

## Upstream baseline

- Upstream project: OpenSimulator
- Baseline release: 0.9.3.0 ("Nessie" Release)
- Release date: 8 November 2024
- Imported archive: `opensim-0.9.3.0-source.tar.gz`
- SHA-256: `8dc78639e47859e76a36a03304c1d44c905aac7807f44c1c0af671d156342eab`
- Original OpenSimulator copyright and license notices remain preserved. The primary OpenSimulator BSD license is stored at `LICENSES/BSD/OpenSimulator-0.9.3.0.txt`; contributor and third-party notices remain available in `CONTRIBUTORS.txt`, `LICENSES/` and `ThirdPartyLicenses/`.

## NexVerse licensing

New original NexVerse contributions are licensed under the **Mozilla Public License 2.0 (MPL-2.0)** unless a file or directory explicitly states otherwise.

Imported OpenSimulator files do **not** lose or replace their existing BSD/third-party licensing. NexVerse changes to inherited files must preserve all applicable copyright and license notices. See `LICENSE_POLICY.md`, `NOTICE.md` and `LICENSES/BSD/README.md`.

## Versioning

The current release is **NexVerse 0.9.3.1**, codename **NEXJAST**.

NexVerse 0.9.3.0 is the first NexVerse release and uses OpenSimulator 0.9.3.0 only as its historical source baseline. From this point forward, NexVerse maintains its own version sequence; OpenSimulator upstream version numbers do not automatically become NexVerse version numbers.

Every NexVerse release or development milestone that changes the product version must update `OpenSim/Framework/VersionInfo.cs` together with the corresponding release documentation.

See `VERSIONING.md` for the versioning policy.

## Roadmap

The active development roadmap is maintained in:

**[`doc/NexVerse/ROADMAP.md`](doc/NexVerse/ROADMAP.md)**

The roadmap covers the NEXJAST legacy-cleanup milestone and the planned World API, identity/social platform, simulator/region/estate control plane, inventory/OAR/IAR, NV$ economy and banking, Groups, Experiences, Search/Places/Land/Destination services, NexVoice, Pathfinding, LSL parity, modern assets/rendering, marketplace, advertising, monitoring, developer platform and the NexVerse 0.9.4.0 consolidation target.

## NexVerse 0.9.3.1 NEXJAST release foundation

The NexVerse 0.9.3.1 release contains the first NexVerse-native assemblies:

- `NexVerse.Core` — platform metadata, RBAC/scope contracts, audit contracts and NexBus foundation.
- `NexVerse.Server.Api` — Robust-hosted World API foundation.

Current World API base:

`http://world.stadt-nexverse.de/api/v1`

See `doc/NexVerse/WORLD_API.md` and `doc/NexVerse/LEGACY_COMPONENTS.md`.

## Development model

NexVerse Server is a continuation and substantial independent development based on the OpenSimulator 0.9.3.0 source baseline. Compatibility with Firestorm, Second Life protocols, Hypergrid and LSL is retained where useful, while obsolete administration and service implementations are progressively replaced by NexVerse-native components.

New major systems should be API-first, testable, observable and documented. Compatibility-sensitive code must be assessed before removal.

## Build baseline

The imported OpenSimulator 0.9.3.0 baseline uses the .NET 8 SDK. The original build instructions are retained in this repository.
