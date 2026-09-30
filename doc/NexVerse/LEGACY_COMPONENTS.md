# NexVerse Legacy Component Inventory

This document tracks inherited OpenSimulator components during the **NEXJAST** cleanup.

Status values:

- **KEEP** — currently required and healthy.
- **COMPAT** — retained specifically for viewer, Hypergrid, LSL or protocol compatibility.
- **REPLACE** — retained temporarily until a NexVerse-native replacement reaches parity.
- **REMOVE** — approved for deletion.
- **REMOVED** — deleted from the active NexVerse source/build/configuration.

| Component | Status | NexVerse direction |
| --- | --- | --- |
| XML-RPC RemoteAdmin | **REMOVED** | Replaced by the NexVerse World API/control plane. |
| VivoxVoiceModule | **REMOVED** | Replaced by planned NexVoice WebRTC/Janus architecture. |
| FreeSwitch voice stack | **REMOVED** | Replaced by planned NexVoice WebRTC/Janus architecture. |
| IRC bridge | **REMOVED** | No longer part of the NexVerse communications architecture. |
| Generated `obj/` trees | **REMOVED** | Build intermediates are excluded through `.gitignore`. |
| OptionalModules example modules | **REMOVED** | BareBones and WebSocket echo sample modules are not part of the production NexVerse source line. |
| LSL XML-RPC RemoteData | **COMPAT** | Must remain while required for LSL RemoteData compatibility. It is not RemoteAdmin. |
| LLLogin / viewer login protocol | **KEEP** | Required for compatible viewers including Firestorm. |
| LLUDP / viewer protocol stack | **KEEP** | Required for viewer compatibility. |
| Hypergrid HG1.5 services | **KEEP** | Required until/supplemented by a future explicitly versioned federation layer. |
| OpenID server | **REMOVED** | Legacy OpenID provider/connector removed. Optional LLLogin protocol fields remain until Firestorm runtime validation. |
| UserStatistics/WebStats | **REMOVED** | Legacy `/SStats`/SQLite/AJAX statistics stack removed; replacement is NexMetrics/OpenTelemetry/API observability. |
| OfflineIM | **REPLACE** | Keep until NexMessaging provides equivalent IM/group/HG behavior. |
| OpenSim Groups addon | **REPLACE** | Keep until NexGroups reaches viewer/HG parity. |
| JsonStore | **REPLACE** | Evaluate against LSL/Experience requirements before removal. |
| Legacy Search services | **REPLACE** | Planned NexSearch service. |
| Existing FSAssets | **KEEP** | Current authoritative asset backend; future NexAsset migration must be compatible. |

## Removal rule

A component marked **REPLACE** must not be deleted until its dependencies have been audited and the NexVerse replacement has passed the relevant compatibility tests.

## Protected compatibility rule

Protocol age alone is not sufficient justification for deletion. LSL, Firestorm and Hypergrid behavior must be preserved intentionally.

## NEXJAST first cleanup wave

Completed source cleanup:

- RemoteAdmin source, project, configuration and standalone plugin artifacts removed;
- Vivox source and configuration removed;
- FreeSwitch source, service project, connectors, handlers, interface, configuration and standalone binaries removed;
- IRC bridge source and configuration removed;
- tracked .NET `obj/` intermediate trees removed;
- legacy OpenID server/provider removed;
- legacy UserStatistics/WebStats module and static `/SStats` assets removed;
- NexVerse.Core and NexVerse.Server.Api introduced as native platform assemblies;
- non-production BareBones/WebSocketEcho example modules removed.

Build and runtime verification remains required before NEXJAST is considered complete.
