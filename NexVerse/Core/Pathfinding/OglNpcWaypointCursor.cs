// SPDX-License-Identifier: MPL-2.0
using System;
using System.Collections.Generic;

namespace NexVerse.Core.Pathfinding
{
    /// <summary>
    /// A bounded waypoint cursor for experimental real NPC path following.
    /// Does not move scene entities or bypass the physics subsystem.
    /// </summary>
    public sealed class OglNpcWaypointCursor
    {
        private readonly OglNavigationPoint[] m_Points;
        private int m_Index;
        private DateTimeOffset m_LastAdvance;

        public OglNpcWaypointCursor(IReadOnlyList<OglNavigationPoint> points,
            DateTimeOffset createdAt, int maximumPoints = 256)
        {
            if (points == null || points.Count == 0 ||
                points.Count > maximumPoints || maximumPoints < 1 || maximumPoints > 1024)
                throw new ArgumentException("Invalid or unbounded NPC navigation route", nameof(points));
            m_Points = new OglNavigationPoint[points.Count];
            for (int i = 0; i < points.Count; ++i)
            {
                OglNavigationPoint p = points[i];
                if (!float.IsFinite(p.X) || !float.IsFinite(p.Y) || !float.IsFinite(p.Z))
                    throw new ArgumentException("Nonfinite waypoint", nameof(points));
                m_Points[i] = p;
            }
            m_LastAdvance = createdAt;
        }

        public bool Completed => m_Index >= m_Points.Length;
        public int Remaining => Math.Max(0, m_Points.Length - m_Index);
        public OglNavigationPoint Current
        {
            get
            {
                if (Completed)
                    throw new InvalidOperationException("NPC route already complete");
                return m_Points[m_Index];
            }
        }

        public bool UpdatePosition(float x, float y, float reachRadius,
            DateTimeOffset now)
        {
            if (!float.IsFinite(x) || !float.IsFinite(y) ||
                !float.IsFinite(reachRadius) || reachRadius < 0.5f ||
                reachRadius > 8f)
                return false;
            float reachSquared = reachRadius * reachRadius;
            bool progressed = false;
            while (!Completed)
            {
                OglNavigationPoint point = m_Points[m_Index];
                float dx = point.X - x, dy = point.Y - y;
                if (dx * dx + dy * dy > reachSquared)
                    break;
                m_Index++;
                m_LastAdvance = now;
                progressed = true;
            }
            return progressed;
        }

        /// <summary>
        /// NPC waypoint arrival in 3D. Existing XY-only cursors are kept for
        /// legacy regression callers, but the live NPC motor MUST use this:
        /// a bridge waypoint must not be consumed from the ground beneath it.
        /// NPC MoveToTarget receives a standing offset of +1m above the surface.
        /// </summary>
        public bool UpdatePosition3D(float x, float y, float z,
            float reachRadius, DateTimeOffset now, float verticalTolerance = 1.5f)
        {
            if (!float.IsFinite(x) || !float.IsFinite(y) ||
                !float.IsFinite(z) || !float.IsFinite(reachRadius) ||
                reachRadius < 0.5f || reachRadius > 8f ||
                !float.IsFinite(verticalTolerance) || verticalTolerance < 0.5f ||
                verticalTolerance > 4f)
                return false;

            float reachSquared = reachRadius * reachRadius;
            bool progressed = false;
            while (!Completed)
            {
                OglNavigationPoint point = m_Points[m_Index];
                float dx = point.X - x, dy = point.Y - y;
                if (dx * dx + dy * dy > reachSquared ||
                    Math.Abs(point.Z + 1.0f - z) > verticalTolerance)
                    break;
                m_Index++;
                m_LastAdvance = now;
                progressed = true;
            }
            return progressed;
        }

        public bool Stalled(DateTimeOffset now, TimeSpan maximumWaypointWait) =>
            !Completed && maximumWaypointWait > TimeSpan.Zero &&
            now - m_LastAdvance > maximumWaypointWait;
    }
}
