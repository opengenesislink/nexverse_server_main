using System;
using System.Linq;
using NexVerse.Core.Pathfinding;

static void Check(bool ok, string message)
{
    if (!ok) throw new InvalidOperationException("OGL NPC navigation: " + message);
}

DateTimeOffset now = DateTimeOffset.FromUnixTimeSeconds(1791568800);
OglNpcWaypointCursor cursor = new(new[] {
    new OglNavigationPoint(2, 2, 20),
    new OglNavigationPoint(6, 2, 20),
    new OglNavigationPoint(10, 2, 20)
}, now);
Check(cursor.Remaining == 3 && !cursor.Completed, "initial cursor");
Check(!cursor.UpdatePosition(0, 0, 1.5f, now), "not reached first waypoint");
Check(cursor.UpdatePosition(2, 2, 1.5f, now.AddSeconds(1)),
    "reaches first waypoint");
Check(cursor.Remaining == 2 && cursor.Current.X == 6, "next target");
Check(!cursor.Stalled(now.AddSeconds(20), TimeSpan.FromSeconds(30)),
    "no premature timeout");
Check(cursor.Stalled(now.AddSeconds(35), TimeSpan.FromSeconds(30)),
    "stuck NPC requires route cancellation");
Check(!cursor.UpdatePosition(float.NaN, 0, 1, now), "reject nonfinite position");
Check(!cursor.UpdatePosition(6, 2, 0.1f, now), "reject unreasonable waypoint radius");
Check(cursor.UpdatePosition(6, 2, 1.5f, now.AddSeconds(36)),
    "progress resets timeout");
Check(!cursor.Stalled(now.AddSeconds(55), TimeSpan.FromSeconds(30)),
    "timeout resets on progress");
Check(cursor.UpdatePosition(10, 2, 1.5f, now.AddSeconds(56)), "final waypoint");
Check(cursor.Completed && cursor.Remaining == 0, "route completion");
try
{
    _ = cursor.Current;
    throw new Exception("complete cursor unexpectedly had a waypoint");
}
catch (InvalidOperationException) { }

try
{
    _ = new OglNpcWaypointCursor(Array.Empty<OglNavigationPoint>(), now);
    throw new Exception("empty waypoint route accepted");
}
catch (ArgumentException) { }

try
{
    _ = new OglNpcWaypointCursor(Enumerable.Range(0, 257)
        .Select(x => new OglNavigationPoint(x, 0, 0)).ToArray(), now);
    throw new Exception("oversized waypoint route accepted");
}
catch (ArgumentException) { }

try
{
    _ = new OglNpcWaypointCursor(new[] {
        new OglNavigationPoint(float.PositiveInfinity, 0, 0) }, now);
    throw new Exception("nonfinite waypoint accepted");
}
catch (ArgumentException) { }

// Two floors can share the same X/Y. Reaching the ground waypoint must
// never falsely consume the corresponding bridge waypoint.
OglNpcWaypointCursor elevated = new(new[] {
    new OglNavigationPoint(2, 2, 0),
    new OglNavigationPoint(2, 2, 8),
    new OglNavigationPoint(6, 2, 8)
}, now);
Check(elevated.UpdatePosition3D(2, 2, 1, 1.5f, now.AddSeconds(1)) &&
      elevated.Remaining == 2 && elevated.Current.Z == 8,
    "ground-level arrival must not consume bridge node at identical XY");
Check(!elevated.UpdatePosition3D(2, 2, 1, 1.5f, now.AddSeconds(2)) &&
      elevated.Remaining == 2,
    "ground agent must not advance through elevated floor");
Check(!elevated.UpdatePosition3D(2, 2, float.NaN, 1.5f, now),
    "nonfinite NPC elevation rejected");
Check(!elevated.UpdatePosition3D(2, 2, 9, 1.5f, now, float.PositiveInfinity),
    "invalid vertical tolerance rejected");
Check(elevated.UpdatePosition3D(2, 2, 9, 1.5f, now.AddSeconds(3)) &&
      elevated.Remaining == 1,
    "correct bridge elevation advances to next waypoint");
Check(!elevated.UpdatePosition3D(6, 2, 1, 1.5f, now.AddSeconds(4)) &&
      elevated.Remaining == 1,
    "lower level cannot complete upper-level goal");
Check(elevated.UpdatePosition3D(6, 2, 9, 1.5f, now.AddSeconds(5)) &&
      elevated.Completed, "bridge goal completes at correct elevation");
Console.WriteLine("OGL NPC waypoint follower: bounds, sequencing, 3D level isolation and stall timeout OK");
