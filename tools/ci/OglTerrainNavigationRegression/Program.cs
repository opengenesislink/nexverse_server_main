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
Check(flat.TryFindWorldPath(1, 1, 15, 15, out var route), "flat route");
Check(route.Count == 4, "shortest diagonal path");
Check(route.All(p => p.Z == 10), "world coordinates contain terrain elevations");
Check(!flat.TryFindWorldPath(-1, 1, 3, 3, out _), "invalid negative location");
Check(!flat.TryFindWorldPath(1, 1, float.NaN, 3, out _), "invalid nonfinite location");
Check(!flat.TryFindWorldPath(1, 1, 16, 3, out _), "region boundary");
Check(!flat.TryFindWorldPath(1, 1, 15, 15, out _, 1), "CPU exploration bound");

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
