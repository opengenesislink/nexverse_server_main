# NexVerse versioning

Current product version: **NexVerse 0.9.3.0**

NexVerse starts from the OpenSimulator 0.9.3.0 source baseline but is maintained as an independent product line.

## Rules

1. `OpenSim/Framework/VersionInfo.cs` is the authoritative runtime product version.
2. Runtime version strings use the product name `NexVerse`.
3. The current release string is `NexVerse 0.9.3.0`.
4. Future NexVerse development increments the NexVerse version independently of OpenSimulator upstream.
5. A version bump must be committed together with the code/release state that introduces it.
6. The historical OpenSimulator baseline references in provenance and licensing documents are not changed when the NexVerse product version advances.

The four-component version format is retained for compatibility with the existing build and assembly versioning scheme.
