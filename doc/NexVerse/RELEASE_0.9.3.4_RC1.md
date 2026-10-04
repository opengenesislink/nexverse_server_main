# NexVerse 0.9.3.4 RC1

NexVerse 0.9.3.4 is the first stable-release candidate after the 0.9.3.2 and 0.9.3.3 development checkpoints.

## Release scope

- NexVerse World API v1 and authenticated control plane.
- Resident identity, Display Names, WebProfileV3, social graph and optional security extensions.
- NexVerse NodeAgent discovery, heartbeat and health telemetry.
- CPU, working-set memory, disk capacity, uptime, region and agent telemetry.
- Simulator maintenance and drain/resume state through scoped NexBus commands.
- Managed region create, placement move and start/stop/restart operations with asynchronous operation tracking and safety checks.
- Interactive grid planner API/UI foundation including VarRegion occupancy validation.
- Estate read/write management, access policy, region assignment and safe Estate deletion.
- Persistent audit, NexBus events, rate limiting, idempotency and CI regression coverage.

## Deliberately deferred

General remote shell/console execution, raw log retrieval and node software update/rollback are not exposed as broad 0.9.3.4 APIs. The release uses bounded authenticated operations instead. Deployment/update orchestration can be added in a later milestone without weakening the control-plane security model.

## Upgrade note

0.9.3.4 retains the OpenSimulator 0.9.3.0 historical source baseline and NexVerse compatibility layers. Existing 0.9.3.1 installations should back up databases, region INI files and NexVerse data stores before upgrading.

## RC validation

RC1 must pass the complete NexVerse CI pipeline on the release candidate commit. Runtime smoke tests and deployment-specific TLS configuration remain mandatory before exposing privileged World API authentication publicly.
