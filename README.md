# NexVerse Server

NexVerse Server is an independent virtual-world server platform developed by OpenGenesisLink.

This repository starts from the published **OpenSimulator 0.9.3.0 ("Nessie") Release** source package. It is intentionally maintained as its own repository rather than as a GitHub fork. The long-term objective is an independent NexVerse development line whose architecture, services, protocols, modules, tooling and branding may diverge substantially from OpenSimulator.

## Upstream baseline

- Upstream project: OpenSimulator
- Baseline release: 0.9.3.0 ("Nessie" Release)
- Release date: 8 November 2024
- Imported archive: `opensim-0.9.3.0-source.tar.gz`
- SHA-256: `8dc78639e47859e76a36a03304c1d44c905aac7807f44c1c0af671d156342eab`
- Original OpenSimulator notices remain in the source tree, including `LICENSE.txt`, `CONTRIBUTORS.txt` and third-party notices.

## NexVerse licensing

New original NexVerse contributions are licensed under the **Mozilla Public License 2.0 (MPL-2.0)** unless a file or directory explicitly states otherwise.

Imported OpenSimulator files do **not** lose or replace their existing BSD/third-party licensing. NexVerse changes to inherited files must preserve all applicable notices. See `LICENSE_POLICY.md` and `NOTICE.md`.

## Development model

NexVerse Server is a continuation and substantial independent development based on the OpenSimulator 0.9.3.0 source baseline. Compatibility with OpenSimulator and Second Life protocols may be retained where useful, but NexVerse is expected to develop its own components and implementation choices over time.

## Build baseline

OpenSimulator 0.9.3.0 uses the .NET 8 SDK. The original build instructions are retained in this repository.
