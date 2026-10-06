# OpenGenesisLINK versioning

Current stable release: **NexVerse 0.9.3.4**  
Active development line: **OpenGenesisLINK v0.9.3.8 Dev**  
Active milestone: **Search, Places, Land and Destination Guide — Chapter 12 completed**  
Next architectural consolidation line: **OpenGenesisLINK v0.9.4.0**  
Completed milestone codename: **NEXJAST**

OpenGenesisLINK continues from the OpenSimulator 0.9.3.0 source baseline and the former NexVerse Server development line, but is maintained as an independent server-platform product. NexVerse remains the grid/world deployment name.

## Rules

1. `OpenSim/Framework/VersionInfo.cs` is the authoritative runtime product version.
2. Runtime version strings use the product name `OpenGenesisLINK`; the short administrator-facing form may use `OGL`.
3. Development builds use the `Dev` flavour.
4. Release candidates use `RC1`, `RC2`, `RC3` as required.
5. Final releases use the `Release` flavour and omit the flavour suffix from the normal runtime version string.
6. OpenGenesisLINK versions advance independently of the OpenSimulator upstream version.
7. A version bump must be committed together with the development/release state that introduces it.
8. Historical OpenSimulator baseline references and applicable BSD/third-party notices remain unchanged while inherited licensed material remains.
9. Milestone scope and version progression are documented in `doc/NexVerse/ROADMAP.md`.
10. Compatibility-sensitive internal `NexVerse.*` namespaces, API paths and configuration keys are migrated separately rather than renamed blindly.

## Current line

- `NexVerse 0.9.3.0` — first NexVerse release based on the imported OpenSimulator 0.9.3.0 baseline.
- `NexVerse 0.9.3.1` — **NEXJAST**, released 2 October 2026 after completing the legacy-cleanup and platform-foundation milestone.
- `NexVerse 0.9.3.2 Dev` — **World API v1** development checkpoint, completed 3 October 2026 with 36/36 explicit roadmap criteria satisfied; no separate stable 0.9.3.2 release was produced.
- `NexVerse 0.9.3.3 Dev` — completed identity/profile/social development checkpoint within the 0.9.3.4 release train.
- `NexVerse 0.9.3.4` — completed stable release for Simulator, Region and Estate Control Plane.
- `OpenGenesisLINK v0.9.3.5 Dev` — completed development line for Inventory, OAR/IAR, persistent Job Engine and NexBus.
- `OpenGenesisLINK v0.9.3.6 Dev` — completed development line for NV$ economy, banking, commerce and land flows.
- `OpenGenesisLINK v0.9.3.7 Dev` — completed development line for NexGroups parity, native NexExperiences and Experience LSL.
- `OpenGenesisLINK v0.9.3.8 Dev` — current development line; NexSearch, Places, Land Portal and Destination Guide are implemented with optional viewer discovery capabilities.
- `OpenGenesisLINK v0.9.3.5 RC1+` — future release-candidate phase after the 0.9.3.5 definition of done is satisfied.
- `OpenGenesisLINK v0.9.3.5` — next stable release after validation of the active milestone.
- `OpenGenesisLINK v0.9.4.0` — Generation 1 consolidation line for broader OGL branding, German administration surfaces and the native addon subsystem.

The product-name transition does not rewrite provenance. OpenSimulator 0.9.3.0 remains the historical source baseline, and inherited licensing remains attached to inherited code until independently replaced.

The four-component version format is retained for compatibility with the existing build and assembly versioning scheme. Protocol and API compatibility levels are versioned independently where appropriate so the product version does not encode every internal protocol revision.
