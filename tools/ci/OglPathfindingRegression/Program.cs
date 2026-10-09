using System;
using System.Collections.Generic;
using NexVerse.Core.Pathfinding;

static void Check(bool value, string name)
{
    if (!value)
        throw new InvalidOperationException("OGL A* regression failed: " + name);
}

OglGridPathfinder open = new(8, 8, (_, _) => true);
Check(open.TryFindPath(new GridCell(0, 0), new GridCell(7, 7), out var diagonal),
    "open diagonal route");
Check(diagonal.Count == 8, "shortest octile route");
Check(diagonal[0].Equals(new GridCell(0, 0)) &&
      diagonal[^1].Equals(new GridCell(7, 7)), "path endpoints");
Check(open.TryFindPath(new GridCell(2, 2), new GridCell(2, 2), out var same) &&
      same.Count == 1, "same-point route");
Check(!open.TryFindPath(new GridCell(-1, 0), new GridCell(3, 3), out _),
    "out-of-bounds origin must fail");
Check(!open.TryFindPath(new GridCell(0, 0), new GridCell(99, 99), out _),
    "out-of-bounds destination must fail");
Check(!open.TryFindPath(new GridCell(0, 0), new GridCell(7, 7), out _, 1),
    "expansion quota prevents long-running searches");

OglGridPathfinder blockedCorner = new(2, 2,
    (x, y) => !((x == 1 && y == 0) || (x == 0 && y == 1)));
Check(!blockedCorner.TryFindPath(new GridCell(0, 0), new GridCell(1, 1), out _),
    "diagonal must not cut through a blocked corner");

OglGridPathfinder separated = new(7, 7, (x, y) => x != 3);
Check(!separated.TryFindPath(new GridCell(1, 2), new GridCell(5, 2), out _),
    "unbroken wall must be impassable");
OglGridPathfinder gateway = new(7, 7, (x, y) => x != 3 || y == 5);
Check(gateway.TryFindPath(new GridCell(1, 2), new GridCell(5, 2), out var viaGate),
    "detour through navigation gateway");
Check(viaGate.Count > 5 && viaGate.Any(n => n.Equals(new GridCell(3, 5))),
    "detour must use gateway");
for (int i = 1; i < viaGate.Count; i++)
{
    GridCell previous = viaGate[i - 1], current = viaGate[i];
    Check(Math.Abs(previous.X - current.X) <= 1 &&
          Math.Abs(previous.Y - current.Y) <= 1,
        "only adjacent navigation cells allowed");
    Check(gateway.IsWalkable(current), "path cannot traverse blocked cells");
}

OglGridPathfinder afterTerrainChange = new(7, 7, (x, y) => x != 3 || y == 4);
Check(afterTerrainChange.TryFindPath(new GridCell(1, 2), new GridCell(5, 2), out var newRoute)
      && newRoute.Any(n => n.Equals(new GridCell(3, 4))),
    "fresh snapshots reflect edited obstacle map");

try
{
    _ = new OglGridPathfinder(2000, 2000, (_, _) => true);
    throw new InvalidOperationException("grid-size quota must be enforced");
}
catch (ArgumentOutOfRangeException) { }

Console.WriteLine("OGL pathfinding A* core: bounded 8-way traversal, corner rules, obstacles and snapshot rebuild OK");
