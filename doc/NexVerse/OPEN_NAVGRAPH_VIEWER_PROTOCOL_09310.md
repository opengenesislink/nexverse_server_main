# OpenGenesisLINK native pathfinding transport — OGLNAVGRAPH/1

The future OpenGenesisLINK Viewer will use a separate open protocol,
**not** Linden Lab / Havok `RetrieveNavMeshSrc` binary data. This
implements a safe **server topology handoff**, not a polygon rendering
engine or a completely finished character system.

## Wire protocol

Capability name: `OGLRetrieveNavGraph`; HTTP GET over the per-agent
login CAPS URL. Successful LLSD/XML response fields:
`format="OGLNAVGRAPH/1"`, `region_id` (UUID),
`revision` (positive integer) and `data` (binary).

The binary `data` is little-endian:

| Field | Encoding |
| --- | --- |
| `OGLN` | 4-byte ASCII magic |
| Version | signed 32-bit integer = 1 |
| Epoch | signed 32-bit integer > 0 |
| Cell size | IEEE-754 float32 |
| Node count | signed 32-bit |
| Directed arc count | signed 32-bit |
| Each node | 3 × int32 X/Y/layer; float32 Z/radius; uint8 walkable |
| Each arc | 2 × int32 source/target; uint8 offmesh |
| Footer | SHA-256 digest of previous bytes, 32 bytes |

Maximum 65,536 nodes, 600,000 directed arcs, 12 MiB binary.
Planar edges are written twice (both directions); portals are written
only in their validated travel direction. Both arcs and nodes have
deterministic ordering, allowing client-side revision caches.

**Important:** The source must be a current certified navigation graph
with explicit physics-verified planar edges. Legacy terrain-only A*
is **not** exportable. Epochs reject old geometry snapshots. A dirty
or missing graph yields HTTP 503. Invalidated regions require a fresh
read after a successful rebuild. Session-lost/child/NPC access returns
HTTP 410; repeated calls are limited to one per three seconds and
HTTP 429 otherwise. The per-avatar gate uses atomic compare-and-swap;
two simultaneous GET requests **cannot** both pass by racing between
the last-read check and its update. Time reversal fails closed and
another authorized resident retains an independent quota.

## Simulator configuration

```ini
[OGLPathfinding]
    Enabled = true
    UseVerifiedLayeredSurfaces = true
    PhysicsRaycastLayeredSurfaces = true
    PhysicsMultiRayClearance = true
    PhysicsVerifiedTransitions = true
    OpenNavGraphCaps = true
    FirestormNavMeshCaps = false
```

This is **test-only**, not a safe production preset. Invalid geometry
or unsupported physics must fail closed. Enabling `OpenNavGraphCaps`
does nothing for the current Firestorm build, which does not know this
capability and whose open-source LLPathingLib is a stub. These data are
not a Havok NavMesh, Detour mesh or GL render buffer.

## Later viewer work

The future fork must implement a client decoder for this exact schema,
a full 3D path overlay, picking/editing affordances, and a properly
licensed open navmesh runtime. A Recast/Detour polygon bake and an
integrated convex/capsule sweep are still needed for complete editor
parity and safe avatar movement. No current feature flag pretends
to enable the grey Firestorm Pathfinding pane.

OGL Voice remains deferred.
