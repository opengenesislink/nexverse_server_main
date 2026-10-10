// SPDX-License-Identifier: MPL-2.0
using System;

namespace NexVerse.Core.Pathfinding
{
    /// <summary>World-space foot location for the experimental volume test.</summary>
    public readonly struct OglClearancePoint
    {
        public readonly float X, Y, Z;
        public OglClearancePoint(float x, float y, float z)
        {
            X = x;
            Y = y;
            Z = z;
        }
    }

    /// <summary>
    /// Deliberately conservative MULTI-RAY corridor check, NOT a continuous
    /// capsule sweep. A physics adapter must return true only for an
    /// unoccluded segment. Every missing/failed ray is treated as blocked.
    /// This helps reject common overheads and walls but cannot certify
    /// corner-grazing/complex convex geometry, so not safe as the sole basis
    /// for production Character motion.
    /// </summary>
    public static class OglMultiRayAgentClearance
    {
        /// <summary>
        /// Performs 5 vertical standing-height probes at each endpoint, and
        /// 9 horizontal/directional rays on three vertical bands and three
        /// lateral offsets. All sample points must stay within the region
        /// (the adapter MUST enforce that). Caller bounds total ray count
        /// and the time spent in its native physics backend.
        /// </summary>
        public static bool IsCorridorClear(
            OglClearancePoint from, OglClearancePoint to,
            float radius, float height,
            Func<OglClearancePoint, OglClearancePoint, bool> rayClear)
        {
            if (rayClear == null || !Valid(from) || !Valid(to) ||
                !float.IsFinite(radius) || radius < 0.125f || radius > 0.6f ||
                !float.IsFinite(height) || height < 1f || height > 3f)
                return false;

            float dx = to.X - from.X, dy = to.Y - from.Y;
            float distance = MathF.Sqrt(dx * dx + dy * dy);
            if (!float.IsFinite(distance) || distance > 16.1f ||
                MathF.Abs(to.Z - from.Z) > 1.5f)
                return false;

            float lateralX = distance > 0.01f ? -dy / distance : 0f;
            float lateralY = distance > 0.01f ? dx / distance : 1f;

            // Nonzero foot offset avoids mistaking a legitimate floor for
            // a wall; a clearance gap below this height remains unverified.
            float bottom = 0.12f;
            float top = height - 0.12f;
            if (top <= bottom) return false;
            float[] offsets = { 0f, -radius * 0.8f, radius * 0.8f };

            foreach (OglClearancePoint foot in new[] { from, to })
            {
                if (!rayClear(
                    Offset(foot, 0f, 0f, bottom),
                    Offset(foot, 0f, 0f, top)))
                    return false;
                foreach (float side in offsets)
                {
                    if (side == 0f) continue;
                    if (!rayClear(
                        Offset(foot, side * lateralX, side * lateralY, bottom),
                        Offset(foot, side * lateralX, side * lateralY, top)))
                        return false;
                }
                // Additional forward/back probes catch narrow overhangs.
                if (!rayClear(
                    Offset(foot, radius * 0.8f, 0f, bottom),
                    Offset(foot, radius * 0.8f, 0f, top)) ||
                    !rayClear(
                        Offset(foot, -radius * 0.8f, 0f, bottom),
                        Offset(foot, -radius * 0.8f, 0f, top)))
                    return false;
            }

            if (distance <= 0.01f) return true;
            float[] bands = { bottom, (bottom + top) * 0.5f, top };
            foreach (float band in bands)
            foreach (float side in offsets)
            {
                float x = side * lateralX, y = side * lateralY;
                if (!rayClear(Offset(from, x, y, band),
                              Offset(to, x, y, band)))
                    return false;
            }
            return true;
        }

        private static OglClearancePoint Offset(
            OglClearancePoint p, float x, float y, float z) =>
            new(p.X + x, p.Y + y, p.Z + z);

        private static bool Valid(OglClearancePoint p) =>
            float.IsFinite(p.X) && float.IsFinite(p.Y) &&
            float.IsFinite(p.Z);
    }
}
