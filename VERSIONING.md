# NexVerse versioning

Current stable release: **NexVerse 0.9.3.1**  
Active development line: **NexVerse 0.9.3.2 Dev**  
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

The four-component version format is retained for compatibility with the existing build and assembly versioning scheme.

Larger protocol and API compatibility levels should be versioned independently where appropriate so that the product version does not have to encode every internal protocol revision.
