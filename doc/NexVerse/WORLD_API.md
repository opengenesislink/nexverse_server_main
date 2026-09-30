# NexVerse World API

The NexVerse World API is the new Robust-hosted control-plane API.

Current development endpoint:

`http://world.stadt-nexverse.de/api/v1`

NexVerse currently runs Robust directly on TCP port 80 without a reverse proxy. Therefore `world.stadt-nexverse.de` should resolve to the same Robust host as the public grid endpoint. The World API uses its own `/api/v1` path namespace.

## Foundation endpoints

- `GET /api/v1` — service metadata
- `GET /api/v1/health` — API health
- `GET /api/v1/version` — NexVerse/API/protocol versions
- `GET /api/v1/capabilities` — compatibility/capability levels
- `GET /api/v1/openapi.json` — OpenAPI 3.1 document

These foundation endpoints are public and read-only.

Administrative write endpoints are intentionally not exposed until the authentication and scoped authorization layer is connected to NexVerse identity services.

## Architecture

The World API is implemented in the NexVerse-native `NexVerse.Server.Api` assembly and depends on `NexVerse.Core`.

The initial composition root provides:

- NexBus event publication for API requests;
- audit event recording;
- correlation IDs;
- API version headers;
- RBAC/scope contracts in NexVerse.Core;
- OpenAPI metadata for citizen/admin audiences.

Future privileged endpoints will use scoped authentication such as `users:write`, `regions:manage`, `estates:manage` and `economy:transfer`.
