using System;
using System.Linq;
using NexVerse.Core.Pathfinding;

static void Check(bool condition, string context)
{
    if (!condition) throw new InvalidOperationException("Terrain navigation regression: " + context);
}

OglTerrainNavigationSnapshot flat = OglTerrainNavigationSnapshot.Build(
    16, 16, 4, 0, 0.5f, (_, _) => 10.0f);
Check(flat.GridWidth == 4 && flat.GridHeight == 4, "grid cell dimensions");
Check(flat.TryFindStaticTerrainRoute(1, 1, 10, 15, 15, 10, 0.5f,
    out var staticWaypoints, out int staticStatus) &&
    staticStatus == 0 && staticWaypoints.Count > 1,
    "llGetStaticPath static terrain route includes waypoints + success");
Check(!flat.TryFindStaticTerrainRoute(-1, 1, 10, 15, 15, 10, 0.5f,
    out _, out int badStart) && badStart == 2,
    "llGetStaticPath invalid start returns PU_FAILURE_INVALID_START");
Check(!flat.TryFindStaticTerrainRoute(1, 1, 10, 16, 15, 10, 0.5f,
    out _, out int badGoal) && badGoal == 3,
    "llGetStaticPath invalid goal returns PU_FAILURE_INVALID_GOAL");
Check(!flat.TryFindStaticTerrainRoute(1, 1, 10, 15, 15, 10, 0.01f,
    out _, out int badRadius) && badRadius == 0xF4240,
    "llGetStaticPath invalid radius fails closed");
Check(flat.TryFindWorldPath(1, 1, 15, 15, out var route), "flat route");
Check(route.Count == 4, "shortest diagonal path");
Check(route.All(p => p.Z == 10), "world coordinates contain terrain elevations");
Check(!flat.TryFindWorldPath(-1, 1, 3, 3, out _), "invalid negative location");
Check(!flat.TryFindWorldPath(1, 1, float.NaN, 3, out _), "invalid nonfinite location");
Check(!flat.TryFindWorldPath(1, 1, 16, 3, out _), "region boundary");
Check(!flat.TryFindWorldPath(1, 1, 15, 15, out _, 1), "CPU exploration bound");

Check(flat.TryFindNearestTerrainPoint(1, 1, 10, 4, out var nearest),
    "closest terrain point exists");
Check(nearest.Z == 10 && nearest.X >= 0 && nearest.Y >= 0,
    "nearest point has correct sampled terrain height");
Check(!flat.TryFindNearestTerrainPoint(1, 1, 10, 0.5f, out _),
    "nearest point obeys requested 3D distance");
Check(!flat.TryFindNearestTerrainPoint(1, 1, 10, 65, out _),
    "nearest point has a hard computation bound");
Check(!flat.TryFindNearestTerrainPoint(float.NaN, 1, 10, 20, out _),
    "nearest point rejects nonfinite input");

OglTerrainNavigationSnapshot obstacle = OglTerrainNavigationSnapshot.Build(
    16, 16, 4, 0, 1, (_, _) => 10f,
    (x, y) => x >= 4 && x < 8 && y < 12);
Check(obstacle.TryFindWorldPath(1, 1, 13, 1, out var around),
    "route through open gateway");
Check(around.Any(p => p.Y > 12), "navigation detours around static obstacle");

OglTerrainNavigationSnapshot blocked = OglTerrainNavigationSnapshot.Build(
    16, 16, 4, 0, 1, (_, _) => 10f,
    (x, y) => x >= 4 && x < 8);
Check(!blocked.TryFindWorldPath(1, 1, 13, 1, out _),
    "complete wall blocks crossing");
Check(!blocked.TryFindStaticTerrainRoute(1, 1, 10, 13, 1, 10, 0.5f,
    out _, out int blockedStatus) && blockedStatus == 4,
    "llGetStaticPath wall returns PU_FAILURE_UNREACHABLE");

// Agent-radius clearance: a one-cell-wide passage is walkable for a
// small avatar, but its centre is too near blocked tiles for a wider body.
OglTerrainNavigationSnapshot narrow = OglTerrainNavigationSnapshot.Build(
    20, 20, 4, 0, 1, (_, _) => 10f,
    (_, y) => y < 4 || y >= 8 && y < 12);
Check(narrow.TryFindStaticTerrainRoute(6, 6, 10, 14, 6, 10, 1f,
    out var narrowRoute, out int smallStatus) && smallStatus == 0 &&
    narrowRoute.Count == 3,
    "narrow passage allows a one metre radius agent");
Check(!narrow.TryFindStaticTerrainRoute(6, 6, 10, 14, 6, 10, 2.5f,
    out _, out int largeStatus) && largeStatus == 2,
    "large avatar clearance rejects corridor touching obstacles");

OglTerrainNavigationSnapshot openWide = OglTerrainNavigationSnapshot.Build(
    20, 20, 4, 0, 1, (_, _) => 10f);
Check(openWide.TryFindStaticTerrainRoute(6, 6, 10, 14, 6, 10, 2.5f,
    out _, out int openWideStatus) && openWideStatus == 0,
    "large avatar succeeds on sufficiently clear terrain");

// Static 3D prim/mesh bounds are projected only when their elevation
// overlaps a walker standing on the terrain. The voxel projection is
// deliberately conservative for non-rectangular mesh interiors.
OglStaticCollisionAabb wall = new(8, 0, 9.8f, 12, 20, 12.5f);
bool[] wallMask = OglTerrainStaticObstacles.Project(
    20, 20, 4, 1.8f, (_, _) => 10f, new[] { wall });
Check(wallMask[2] && wallMask[7] && !wallMask[0],
    "ground-level static object projected onto correct walkability cells");
OglTerrainNavigationSnapshot withWall = OglTerrainNavigationSnapshot.Build(
    20, 20, 4, 0, 1f, (_, _) => 10f,
    (x, y) => wallMask[(y / 4) * 5 + x / 4]);
Check(!withWall.TryFindStaticTerrainRoute(6, 10, 10, 18, 10, 10, 0.5f,
    out _, out int projectedWallStatus) && projectedWallStatus == 4,
    "static prim collision across a region must block a terrain route");
bool[] highBridge = OglTerrainStaticObstacles.Project(
    20, 20, 4, 1.8f, (_, _) => 10f,
    new[] { new OglStaticCollisionAabb(8, 0, 14, 12, 20, 16) });
Check(!highBridge.Any(blocked => blocked),
    "raised bridge above walker height must not block terrain below");
bool[] rebuiltNoWall = OglTerrainStaticObstacles.Project(
    20, 20, 4, 1.8f, (_, _) => 10f,
    Array.Empty<OglStaticCollisionAabb>());
Check(!rebuiltNoWall.Any(blocked => blocked),
    "obstacle removal yields an empty projected snapshot");
bool excessiveVisitsRejected = false;
try
{
    OglTerrainStaticObstacles.Project(20, 20, 4, 1.8f,
        (_, _) => 10f, new[] { wall }, maxCellVisits: 1);
}
catch (InvalidOperationException) { excessiveVisitsRejected = true; }
Check(excessiveVisitsRejected, "collider-cell enumeration is budgeted");
bool invalidColliderRejected = false;
try
{
    _ = new OglStaticCollisionAabb(0, 0, 0, float.NaN, 2, 2);
}
catch (ArgumentOutOfRangeException) { invalidColliderRejected = true; }
Check(invalidColliderRejected, "nonfinite static prim bounds rejected");

OglTerrainNavigationSnapshot steep = OglTerrainNavigationSnapshot.Build(
    16, 16, 4, 0, 0.5f, (x, _) => x >= 8 ? 30f : 10f);
Check(!steep.TryFindWorldPath(1, 1, 13, 1, out _),
    "cliff is not traversable");
OglTerrainNavigationSnapshot underwater = OglTerrainNavigationSnapshot.Build(
    16, 16, 4, 10, 1f, (x, _) => x >= 8 ? 9f : 11f);
Check(!underwater.TryFindWorldPath(1, 1, 13, 1, out _),
    "underwater cells are inaccessible");

OglTerrainNavigationSnapshot edited = OglTerrainNavigationSnapshot.Build(
    16, 16, 4, 0, 1, (_, _) => 10f);
Check(edited.TryFindWorldPath(1, 1, 13, 1, out _),
    "independent rebuilt terrain snapshot after removal");

OglTerrainNavigationSnapshot irregular = OglTerrainNavigationSnapshot.Build(
    10, 10, 4, 0, 1, (_, _) => 5f);
Check(irregular.GridWidth == 3 && irregular.GridHeight == 3,
    "non-divisible region extents");
Check(irregular.TryFindWorldPath(9.9f, 9.9f, 0, 0, out _),
    "partial edge tiles work");

try
{
    OglTerrainNavigationSnapshot.Build(256, 256, 0, 0, 1, (_, _) => 5);
    throw new Exception("invalid tile size unexpectedly accepted");
}
catch (ArgumentOutOfRangeException) {}

Console.WriteLine("OGL terrain navigation snapshot: slope, water, obstacles, dirty rebuild, bounds and world coordinates OK");
