using System;
using System.Collections.Generic;
using GeometryLib;

namespace electrostat
{
    /// <summary>
    /// One conductor/loop registered with a <see cref="GeometryClipper"/>.
    /// </summary>
    /// <param name="SurfaceId">User-assigned id (e.g. surface index in <see cref="Geometry.Surfaces"/>).</param>
    /// <param name="Loop">The boundary loop (lines + arcs).</param>
    /// <param name="IsHole">
    /// <c>true</c> if the loop bounds the *outside* of a conductor (i.e. it's a hole in the
    /// fluid domain). Streamlines moving from oil into the conductor's interior cross such
    /// a loop from outside to inside; either direction counts as a hit for clipping.
    /// </param>
    public readonly record struct ClipLoop(int SurfaceId, GeomLineLoop Loop, bool IsHole = true);

    /// <summary>
    /// Result of a successful streamline-vs-boundary clip.
    /// </summary>
    public readonly record struct ClipHit(
        int SurfaceId,
        double X, double Y,
        /// <summary>Segment parameter t in [0,1] along the input segment (a + t*(b-a)).</summary>
        double T);

    /// <summary>
    /// Clips a candidate streamline step against a collection of <see cref="GeomLineLoop"/>
    /// boundaries (conductors / barriers). Returns the closest intersection along the
    /// step, if any.
    /// </summary>
    /// <remarks>
    /// Uses a simple bounding-box prefilter per loop. For axisymmetric (r,z)
    /// transformer cross-sections the loop count is small (~tens), so this is plenty
    /// fast; if it ever isn't, replace with a segment AABB tree.
    /// </remarks>
    public sealed class GeometryClipper
    {
        private readonly List<Entry> _entries;

        private readonly struct Entry
        {
            public readonly ClipLoop Clip;
            public readonly double MinX, MinY, MaxX, MaxY;
            public Entry(ClipLoop clip)
            {
                Clip = clip;
                var (minX, maxX, minY, maxY) = clip.Loop.GetBoundingBox();
                MinX = minX; MinY = minY; MaxX = maxX; MaxY = maxY;
            }
        }

        public GeometryClipper(IEnumerable<ClipLoop> loops)
        {
            _entries = new List<Entry>();
            foreach (var l in loops) _entries.Add(new Entry(l));
        }

        /// <summary>Convenience: build a clipper from every non-domain surface in a <see cref="Geometry"/>.</summary>
        /// <param name="geometry">Source geometry. Surface 0 is treated as the outer domain and skipped.</param>
        public static GeometryClipper FromGeometry(Geometry geometry, bool includeOuterBoundary = false)
        {
            var loops = new List<ClipLoop>();
            for (int i = 0; i < geometry.Surfaces.Count; i++)
            {
                var surf = geometry.Surfaces[i];
                if (i == 0)
                {
                    if (includeOuterBoundary)
                        loops.Add(new ClipLoop(i, surf.Boundary, IsHole: false));
                    continue;
                }
                loops.Add(new ClipLoop(i, surf.Boundary, IsHole: true));
            }
            return new GeometryClipper(loops);
        }

        /// <summary>
        /// Find the closest intersection (smallest <c>t</c>) of the segment
        /// (x0,y0)→(x1,y1) with any registered loop.
        /// </summary>
        public bool TryClipSegment(double x0, double y0, double x1, double y1, out ClipHit hit)
        {
            hit = default;
            double bestT = double.PositiveInfinity;
            bool found = false;

            // Segment AABB for prefilter.
            double sMinX = Math.Min(x0, x1), sMaxX = Math.Max(x0, x1);
            double sMinY = Math.Min(y0, y1), sMaxY = Math.Max(y0, y1);

            foreach (var e in _entries)
            {
                if (sMaxX < e.MinX || sMinX > e.MaxX) continue;
                if (sMaxY < e.MinY || sMinY > e.MaxY) continue;

                foreach (var entity in e.Clip.Loop.Boundary)
                {
                    if (entity is GeomLine line)
                    {
                        if (TrySegSegIntersect(
                                x0, y0, x1, y1,
                                line.pt1.x, line.pt1.y, line.pt2.x, line.pt2.y,
                                out double t))
                        {
                            if (t < bestT)
                            {
                                bestT = t;
                                hit = new ClipHit(e.Clip.SurfaceId,
                                    x0 + t * (x1 - x0), y0 + t * (y1 - y0), t);
                                found = true;
                            }
                        }
                    }
                    else if (entity is GeomArc arc)
                    {
                        if (TrySegArcIntersect(x0, y0, x1, y1, arc, out double t))
                        {
                            if (t < bestT)
                            {
                                bestT = t;
                                hit = new ClipHit(e.Clip.SurfaceId,
                                    x0 + t * (x1 - x0), y0 + t * (y1 - y0), t);
                                found = true;
                            }
                        }
                    }
                }
            }

            return found;
        }

        // --- segment / segment ---
        // Returns the smallest t in (eps, 1] for which p0 + t*(p1-p0) lies on (q0,q1).
        // The lower bound excludes the seed point itself when a streamline starts on
        // a boundary (ε ≈ 1e-9 keeps us off the start; the integrator's surface offset
        // handles the rest).
        private static bool TrySegSegIntersect(
            double p0x, double p0y, double p1x, double p1y,
            double q0x, double q0y, double q1x, double q1y,
            out double t)
        {
            t = 0;
            double rx = p1x - p0x, ry = p1y - p0y;
            double sx = q1x - q0x, sy = q1y - q0y;
            double denom = rx * sy - ry * sx;
            if (Math.Abs(denom) < 1e-18) return false; // parallel / colinear

            double dx = q0x - p0x, dy = q0y - p0y;
            double tt = (dx * sy - dy * sx) / denom;
            double uu = (dx * ry - dy * rx) / denom;

            const double tEps = 1e-9;
            const double uEps = 1e-9;
            if (tt <= tEps || tt > 1.0) return false;
            if (uu < -uEps || uu > 1.0 + uEps) return false;

            t = tt;
            return true;
        }

        // --- segment / arc ---
        // Solve |p0 + t*(p1-p0) - C|^2 = R^2 for t in (eps, 1], then verify the hit
        // point's angle lies within the arc's angular sweep.
        private static bool TrySegArcIntersect(
            double p0x, double p0y, double p1x, double p1y,
            GeomArc arc, out double tOut)
        {
            tOut = 0;
            var c = arc.Center;
            double r = arc.Radius;

            double dx = p1x - p0x, dy = p1y - p0y;
            double fx = p0x - c.x, fy = p0y - c.y;

            double A = dx * dx + dy * dy;
            double B = 2.0 * (fx * dx + fy * dy);
            double C = fx * fx + fy * fy - r * r;

            double disc = B * B - 4.0 * A * C;
            if (disc < 0 || A <= 0) return false;
            double sd = Math.Sqrt(disc);

            // Try smaller root first.
            double t1 = (-B - sd) / (2.0 * A);
            double t2 = (-B + sd) / (2.0 * A);

            const double tEps = 1e-9;
            double best = double.PositiveInfinity;
            CheckRoot(t1, p0x, p0y, dx, dy, arc, tEps, ref best);
            CheckRoot(t2, p0x, p0y, dx, dy, arc, tEps, ref best);
            if (double.IsPositiveInfinity(best)) return false;
            tOut = best;
            return true;
        }

        private static void CheckRoot(double t, double p0x, double p0y, double dx, double dy,
            GeomArc arc, double tEps, ref double best)
        {
            if (t <= tEps || t > 1.0) return;
            double hx = p0x + t * dx;
            double hy = p0y + t * dy;
            if (!IsAngleOnArc(arc, hx, hy)) return;
            if (t < best) best = t;
        }

        private static bool IsAngleOnArc(GeomArc arc, double hx, double hy)
        {
            var c = arc.Center;
            double aHit = Math.Atan2(hy - c.y, hx - c.x);
            double aStart = Math.Atan2(arc.StartPt.y - c.y, arc.StartPt.x - c.x);
            double sweep = arc.SweepAngle;

            // Walk from aStart by an amount in [0, |sweep|] in the sweep's direction
            // to see if aHit falls within. Allow a small angular tolerance.
            double delta = aHit - aStart;
            // Normalize delta into the same sense as sweep.
            if (sweep >= 0)
            {
                while (delta < -1e-9) delta += 2.0 * Math.PI;
                return delta <= sweep + 1e-9;
            }
            else
            {
                while (delta > 1e-9) delta -= 2.0 * Math.PI;
                return delta >= sweep - 1e-9;
            }
        }
    }
}
