# NexVerse versioning

Current stable release: **NexVerse 0.9.3.1**  
Active development line: **NexVerse 0.9.3.2 Dev**  
Active release train: **0.9.3.2 → 0.9.3.3 → 0.9.3.4**  
Target stable release: **NexVerse 0.9.3.4**  
Active milestone: **World API v1**  
Completed milestone codename: **NEXJAST**

NexVerse starts from the OpenSimulator 0.9.3.0 source baseline but is maintained as an independent product line.

## Rules

1. `OpenSim/Framework/VersionInfo.cs` is the authoritative runtime product version.
2. Runtime version strings use the product name `NexVerse`.
3. Development builds use the `Dev` flavour.
4. Release candidates use `RC1`, `RC2`, `RC3` as required.
5. Final releases use the `Release` flavour and omit the flavour suffix from the normal runtime version string.
6. Future NexVerse development increments the NexVerse version independently of OpenSimulator upstream.
7. A version bump must be committed together with the development/release state that introduces it.
8. The historical OpenSimulator baseline references in provenance and licensing documents are not changed when the NexVerse product version advances.
9. Milestone scope and version progression are documented in `doc/NexVerse/ROADMAP.md`.

## Current line

- `NexVerse 0.9.3.0` — first NexVerse release based on the imported OpenSimulator 0.9.3.0 baseline.
- `NexVerse 0.9.3.1` — **NEXJAST**, released 2 October 2026 after completing the legacy-cleanup and platform-foundation milestone.
- `NexVerse 0.9.3.2 Dev` — active development line for **NexVerse World API v1**, formally started 2 October 2026.
- `NexVerse 0.9.3.3 Dev` — next development milestone for Identity, Display Names, Profiles and Social Graph; no separate stable 0.9.3.3 release is planned.
- `NexVerse 0.9.3.4 Dev` — final development milestone in the current release train for Simulator, Region and Estate Control Plane.
- `NexVerse 0.9.3.4 RC1+` — release-candidate phase after the 0.9.3.4 scope and release definition of done are satisfied.
- `NexVerse 0.9.3.4` — next planned stable release.

The 0.9.3.2 and 0.9.3.3 milestones are development checkpoints inside the 0.9.3.4 release train, not separate stable releases. Work from 0.9.3.5 or later must not be implemented until 0.9.3.4 has shipped, except for already-existing forward work that is required to finish or stabilize the 0.9.3.4 train.

The four-component version format is retained for compatibility with the existing build and assembly versioning scheme.

Larger protocol and API compatibility levels should be versioned independently where appropriate so that the product version does not have to encode every internal protocol revision.
