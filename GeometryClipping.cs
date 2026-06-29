using System;
using System.Collections.Generic;
using System.Linq;
using GeometryLib;

namespace electrostat
{
    /// <summary>
    /// Utilities for clipping geometry to a rectangular domain boundary.
    /// </summary>
    public static class GeometryClipping
    {
        // Cohen-Sutherland region codes
        private const int INSIDE = 0; // 0000
        private const int LEFT = 1;   // 0001
        private const int RIGHT = 2;  // 0010
        private const int BOTTOM = 4; // 0100
        private const int TOP = 8;    // 1000

        /// <summary>
        /// Compute Cohen-Sutherland region code for a point.
        /// </summary>
        private static int ComputeRegionCode(double x, double y, Domain domain)
        {
            int code = INSIDE;
            if (x < domain.RInner) code |= LEFT;
            else if (x > domain.ROuter) code |= RIGHT;
            if (y < domain.ZLower) code |= BOTTOM;
            else if (y > domain.ZUpper) code |= TOP;
            return code;
        }

        /// <summary>
        /// Clip a line segment to the domain using Cohen-Sutherland algorithm.
        /// Returns null if the line is entirely outside the domain.
        /// </summary>
        public static (GeomPoint p1, GeomPoint p2)? ClipLineSegment(
            double x1, double y1, double x2, double y2, 
            Domain domain, Geometry targetGeometry)
        {
            int code1 = ComputeRegionCode(x1, y1, domain);
            int code2 = ComputeRegionCode(x2, y2, domain);

            while (true)
            {
                if ((code1 | code2) == 0)
                {
                    // Both points inside - accept line
                    return (targetGeometry.AddPoint(x1, y1), targetGeometry.AddPoint(x2, y2));
                }
                else if ((code1 & code2) != 0)
                {
                    // Both points in same outside region - reject line
                    return null;
                }
                else
                {
                    // Line crosses boundary - clip it
                    int codeOut = code1 != 0 ? code1 : code2;
                    double x = 0, y = 0;

                    // Find intersection point with domain boundary
                    if ((codeOut & TOP) != 0)
                    {
                        x = x1 + (x2 - x1) * (domain.ZUpper - y1) / (y2 - y1);
                        y = domain.ZUpper;
                    }
                    else if ((codeOut & BOTTOM) != 0)
                    {
                        x = x1 + (x2 - x1) * (domain.ZLower - y1) / (y2 - y1);
                        y = domain.ZLower;
                    }
                    else if ((codeOut & RIGHT) != 0)
                    {
                        y = y1 + (y2 - y1) * (domain.ROuter - x1) / (x2 - x1);
                        x = domain.ROuter;
                    }
                    else if ((codeOut & LEFT) != 0)
                    {
                        y = y1 + (y2 - y1) * (domain.RInner - x1) / (x2 - x1);
                        x = domain.RInner;
                    }

                    // Replace clipped point and recalculate code
                    if (codeOut == code1)
                    {
                        x1 = x;
                        y1 = y;
                        code1 = ComputeRegionCode(x1, y1, domain);
                    }
                    else
                    {
                        x2 = x;
                        y2 = y;
                        code2 = ComputeRegionCode(x2, y2, domain);
                    }
                }
            }
        }

        /// <summary>
        /// Clip a GeomLine to the domain. Returns null if entirely outside.
        /// </summary>
        public static GeomLine? ClipLine(GeomLine line, Domain domain, Geometry targetGeometry)
        {
            var result = ClipLineSegment(
                line.pt1.x, line.pt1.y,
                line.pt2.x, line.pt2.y,
                domain, targetGeometry);

            return result.HasValue ? targetGeometry.AddLine(result.Value.p1, result.Value.p2) : null;
        }

        /// <summary>
        /// Clip a single edge of a polygon against one domain boundary using Sutherland-Hodgman.
        /// </summary>
        private static List<GeomPoint> ClipPolygonEdge(
            List<GeomPoint> input, 
            int edgeType, 
            Domain domain,
            Geometry targetGeometry)
        {
            var output = new List<GeomPoint>();
            if (input.Count == 0) return output;

            GeomPoint prevPt = input[^1];
            bool prevInside = IsInsideEdge(prevPt, edgeType, domain);

            foreach (var currPt in input)
            {
                bool currInside = IsInsideEdge(currPt, edgeType, domain);

                if (currInside)
                {
                    if (!prevInside)
                    {
                        // Entering - add intersection
                        var intersection = ComputeIntersection(prevPt, currPt, edgeType, domain);
                        output.Add(targetGeometry.AddPoint(intersection.x, intersection.y));
                    }
                    output.Add(currPt);
                }
                else if (prevInside)
                {
                    // Exiting - add intersection
                    var intersection = ComputeIntersection(prevPt, currPt, edgeType, domain);
                    output.Add(targetGeometry.AddPoint(intersection.x, intersection.y));
                }

                prevPt = currPt;
                prevInside = currInside;
            }

            return output;
        }

        /// <summary>
        /// Check if a point is inside a specific edge boundary.
        /// </summary>
        private static bool IsInsideEdge(GeomPoint pt, int edgeType, Domain domain)
        {
            return edgeType switch
            {
                LEFT => pt.x >= domain.RInner,
                RIGHT => pt.x <= domain.ROuter,
                BOTTOM => pt.y >= domain.ZLower,
                TOP => pt.y <= domain.ZUpper,
                _ => true
            };
        }

        /// <summary>
        /// Compute intersection of line segment with domain edge.
        /// </summary>
        private static (double x, double y) ComputeIntersection(
            GeomPoint p1, GeomPoint p2, int edgeType, Domain domain)
        {
            double t;
            switch (edgeType)
            {
                case LEFT:
                    t = (domain.RInner - p1.x) / (p2.x - p1.x);
                    return (domain.RInner, p1.y + t * (p2.y - p1.y));
                case RIGHT:
                    t = (domain.ROuter - p1.x) / (p2.x - p1.x);
                    return (domain.ROuter, p1.y + t * (p2.y - p1.y));
                case BOTTOM:
                    t = (domain.ZLower - p1.y) / (p2.y - p1.y);
                    return (p1.x + t * (p2.x - p1.x), domain.ZLower);
                case TOP:
                    t = (domain.ZUpper - p1.y) / (p2.y - p1.y);
                    return (p1.x + t * (p2.x - p1.x), domain.ZUpper);
                default:
                    return (p1.x, p1.y);
            }
        }

        /// <summary>
        /// Clip a polygon (represented as point list) to domain using Sutherland-Hodgman algorithm.
        /// </summary>
        public static List<GeomPoint> ClipPolygon(List<GeomPoint> vertices, Domain domain, Geometry targetGeometry)
        {
            if (vertices.Count == 0) return vertices;

            var output = new List<GeomPoint>(vertices);

            // Clip against each edge of the domain rectangle
            output = ClipPolygonEdge(output, LEFT, domain, targetGeometry);
            output = ClipPolygonEdge(output, RIGHT, domain, targetGeometry);
            output = ClipPolygonEdge(output, BOTTOM, domain, targetGeometry);
            output = ClipPolygonEdge(output, TOP, domain, targetGeometry);

            return output;
        }

        /// <summary>
        /// Extract polygon vertices from a GeomLineLoop (simplified - assumes lines only, no arcs).
        /// </summary>
        private static List<GeomPoint> ExtractVertices(GeomLineLoop loop)
        {
            var vertices = new List<GeomPoint>();
            foreach (var entity in loop.Boundary)
            {
                if (entity is GeomLine line)
                {
                    vertices.Add(line.pt1);
                }
                else if (entity is GeomArc arc)
                {
                    // For arcs, approximate with start point
                    // TODO: Tessellate arc for more accurate clipping
                    vertices.Add(arc.StartPt);
                }
            }
            return vertices;
        }

        /// <summary>
        /// Clip a GeomLineLoop to the domain.
        /// Returns null if the loop is entirely outside the domain.
        /// Note: This simplified version works best with line-only loops.
        /// </summary>
        public static GeomLineLoop? ClipLineLoop(GeomLineLoop loop, Domain domain, Geometry targetGeometry)
        {
            // Check if loop bounding box intersects domain
            var bbox = loop.GetBoundingBox();
            double minX = bbox.minX;
            double maxX = bbox.maxX;
            double minY = bbox.minY;
            double maxY = bbox.MaxY;

            // Guard against degenerate/invalid geometry (e.g., NaN/Infinity from bad arcs)
            if (double.IsNaN(minX) || double.IsNaN(maxX) || double.IsNaN(minY) || double.IsNaN(maxY) ||
                double.IsInfinity(minX) || double.IsInfinity(maxX) || double.IsInfinity(minY) || double.IsInfinity(maxY))
            {
                Console.WriteLine("Warning: GeomLineLoop has invalid bounds (NaN/Infinity), skipping clip");
                return null;
            }

            if (!domain.Intersects(minX, minY, maxX, maxY))
                return null;

            // Quick accept if entirely inside
            if (domain.FullyContains(minX, minY, maxX, maxY))
            {
                // Re-add each boundary entity through the target geometry's deduplicating
                // builders. Using AddLine / AddCircleArc (not the lower-level AddArc with
                // (start, end, radius, sweep)) is critical here: an arc that two adjacent
                // components share geometrically (e.g. a winding fillet that also bounds
                // an angle ring) must collapse to a SINGLE GeomArc instance, otherwise
                // gmsh tessellates each copy independently and emits duplicate-coordinate
                // interior nodes -- those nodes then get merged by MFEM's Finalize() and
                // end up tying together boundary edges of different physical attributes
                // (the cross-attribute "Conflicting Boundary Conditions" failure).
                var clonedEntities = new List<GeomEntity>();
                foreach (var entity in loop.Boundary)
                {
                    if (entity is GeomLine line)
                    {
                        var p1 = targetGeometry.AddPoint(line.pt1.x, line.pt1.y);
                        var p2 = targetGeometry.AddPoint(line.pt2.x, line.pt2.y);
                        clonedEntities.Add(targetGeometry.AddLine(p1, p2));
                    }
                    else if (entity is GeomArc arc)
                    {
                        var p1 = targetGeometry.AddPoint(arc.StartPt.x, arc.StartPt.y);
                        var p2 = targetGeometry.AddPoint(arc.EndPt.x, arc.EndPt.y);
                        var pc = targetGeometry.AddPoint(arc.Center.x, arc.Center.y);
                        // Preserve traversal direction by passing start/end in the order
                        // the original loop visited them. AddCircleArc dedupes on the
                        // ordered triple (start, center, end), which is exactly what we
                        // want: an identical arc emitted by another component will reuse
                        // this same instance.
                        clonedEntities.Add(targetGeometry.AddCircleArc(p1, pc, p2));
                    }
                }
                var clippedLoop1 = targetGeometry.AddLineLoop(clonedEntities.ToArray());
                clippedLoop1.Tag = loop.Tag;
                return clippedLoop1;
            }

            // Partial intersection - clip segment-by-segment so arcs are preserved.
            var segs = LoopToSegs(loop);
            segs = ClipPass(segs, LEFT, domain);
            segs = ClipPass(segs, RIGHT, domain);
            segs = ClipPass(segs, BOTTOM, domain);
            segs = ClipPass(segs, TOP, domain);

            if (segs.Count < 3)
                return null;

            var clippedEntities = new List<GeomEntity>(segs.Count);
            foreach (var s in segs)
            {
                clippedEntities.Add(s.Build(targetGeometry));
            }

            var clippedLoop2 = targetGeometry.AddLineLoop(clippedEntities.ToArray());
            clippedLoop2.Tag = loop.Tag;
            return clippedLoop2;
        }

        // ----------------------------------------------------------------
        // Segment-aware clipper (preserves arcs across the partial path)
        // ----------------------------------------------------------------

        private abstract class Seg
        {
            public abstract (double x, double y) Start { get; }
            public abstract (double x, double y) End { get; }
            public abstract (double x, double y) PointAt(double t);
            /// <summary>Returns sorted t-values in (0,1) where the segment crosses the edge line.</summary>
            public abstract List<double> EdgeCrossings(int edgeType, Domain domain);
            /// <summary>Returns the sub-segment between t0 and t1 (0 ≤ t0 &lt; t1 ≤ 1).</summary>
            public abstract Seg Sub(double t0, double t1);
            public abstract GeomEntity Build(Geometry target);
        }

        private sealed class LineSeg : Seg
        {
            public double X1, Y1, X2, Y2;
            public LineSeg(double x1, double y1, double x2, double y2)
            { X1 = x1; Y1 = y1; X2 = x2; Y2 = y2; }
            public override (double x, double y) Start => (X1, Y1);
            public override (double x, double y) End => (X2, Y2);
            public override (double x, double y) PointAt(double t)
                => (X1 + t * (X2 - X1), Y1 + t * (Y2 - Y1));
            public override List<double> EdgeCrossings(int edgeType, Domain d)
            {
                var list = new List<double>();
                double t;
                switch (edgeType)
                {
                    case LEFT:
                        if (Math.Abs(X2 - X1) > 1e-12)
                        {
                            t = (d.RInner - X1) / (X2 - X1);
                            if (t > 1e-9 && t < 1.0 - 1e-9) list.Add(t);
                        }
                        break;
                    case RIGHT:
                        if (Math.Abs(X2 - X1) > 1e-12)
                        {
                            t = (d.ROuter - X1) / (X2 - X1);
                            if (t > 1e-9 && t < 1.0 - 1e-9) list.Add(t);
                        }
                        break;
                    case BOTTOM:
                        if (Math.Abs(Y2 - Y1) > 1e-12)
                        {
                            t = (d.ZLower - Y1) / (Y2 - Y1);
                            if (t > 1e-9 && t < 1.0 - 1e-9) list.Add(t);
                        }
                        break;
                    case TOP:
                        if (Math.Abs(Y2 - Y1) > 1e-12)
                        {
                            t = (d.ZUpper - Y1) / (Y2 - Y1);
                            if (t > 1e-9 && t < 1.0 - 1e-9) list.Add(t);
                        }
                        break;
                }
                return list;
            }
            public override Seg Sub(double t0, double t1)
            {
                var (x0, y0) = PointAt(t0);
                var (x1, y1) = PointAt(t1);
                return new LineSeg(x0, y0, x1, y1);
            }
            public override GeomEntity Build(Geometry target)
            {
                var p1 = target.AddPoint(X1, Y1);
                var p2 = target.AddPoint(X2, Y2);
                return target.AddLine(p1, p2);
            }
        }

        private sealed class ArcSeg : Seg
        {
            public double Cx, Cy, R, StartAngle, Sweep; // signed sweep, radians
            public ArcSeg(double cx, double cy, double r, double startAngle, double sweep)
            { Cx = cx; Cy = cy; R = r; StartAngle = startAngle; Sweep = sweep; }
            public override (double x, double y) Start => PointAt(0);
            public override (double x, double y) End => PointAt(1);
            public override (double x, double y) PointAt(double t)
            {
                double a = StartAngle + t * Sweep;
                return (Cx + R * Math.Cos(a), Cy + R * Math.Sin(a));
            }
            public override List<double> EdgeCrossings(int edgeType, Domain d)
            {
                var list = new List<double>();
                bool vertical = edgeType == LEFT || edgeType == RIGHT;
                double edgeVal = edgeType switch
                {
                    LEFT => d.RInner,
                    RIGHT => d.ROuter,
                    BOTTOM => d.ZLower,
                    TOP => d.ZUpper,
                    _ => 0.0
                };

                // Solve circle ∩ axis-aligned line.
                double s = vertical ? (edgeVal - Cx) / R : (edgeVal - Cy) / R;
                if (s < -1.0 - 1e-12 || s > 1.0 + 1e-12) return list;
                s = Math.Clamp(s, -1.0, 1.0);

                double a1, a2;
                if (vertical)
                {
                    a1 = Math.Acos(s);   // [0, π]
                    a2 = -a1;             // [-π, 0]
                }
                else
                {
                    a1 = Math.Asin(s);   // [-π/2, π/2]
                    a2 = Math.PI - a1;
                }

                TryAddT(a1); TryAddT(a2);
                list.Sort();
                return list;

                void TryAddT(double angle)
                {
                    if (Math.Abs(Sweep) < 1e-15) return;
                    // Try a few 2π wraps because Atan2/Acos give principal values.
                    for (int k = -1; k <= 1; k++)
                    {
                        double t = (angle + k * 2.0 * Math.PI - StartAngle) / Sweep;
                        if (t > 1e-9 && t < 1.0 - 1e-9 && !list.Exists(x => Math.Abs(x - t) < 1e-9))
                            list.Add(t);
                    }
                }
            }
            public override Seg Sub(double t0, double t1)
            {
                double newStart = StartAngle + t0 * Sweep;
                double newSweep = (t1 - t0) * Sweep;
                return new ArcSeg(Cx, Cy, R, newStart, newSweep);
            }
            public override GeomEntity Build(Geometry target)
            {
                var (sx, sy) = Start; var (ex, ey) = End;
                var p1 = target.AddPoint(sx, sy);
                var p2 = target.AddPoint(ex, ey);
                // Route through AddCircleArc (3-point form) so the arc dedupes against
                // any arc with the same (start, center, end) triple already in the target
                // geometry. This collapses geometrically-identical fillet arcs that two
                // adjacent components contribute, preventing gmsh from emitting
                // duplicate-coord interior nodes.
                var pc = target.AddPoint(Cx, Cy);
                return target.AddCircleArc(p1, pc, p2);
            }
        }

        /// <summary>
        /// Sutherland–Hodgman style pass against one half-plane, but operating on
        /// segments (lines AND arcs). Inside intervals are emitted as sub-segments;
        /// gaps between consecutive inside intervals are bridged with a straight
        /// connector line that lies along the clip edge.
        /// </summary>
        private static List<Seg> ClipPass(List<Seg> input, int edgeType, Domain domain)
        {
            var output = new List<Seg>();
            if (input.Count == 0) return output;

            (double x, double y)? lastInsideEnd = null;
            (double x, double y)? firstInsideStart = null;

            foreach (var seg in input)
            {
                var ts = new List<double> { 0.0 };
                ts.AddRange(seg.EdgeCrossings(edgeType, domain));
                ts.Add(1.0);
                ts.Sort();

                for (int i = 0; i < ts.Count - 1; i++)
                {
                    double t0 = ts[i], t1 = ts[i + 1];
                    if (t1 - t0 < 1e-12) continue;

                    var midPt = seg.PointAt(0.5 * (t0 + t1));
                    if (!IsInsideEdge(midPt.x, midPt.y, edgeType, domain)) continue;

                    var sub = seg.Sub(t0, t1);
                    var subStart = sub.Start;
                    var subEnd = sub.End;

                    if (lastInsideEnd.HasValue)
                    {
                        var le = lastInsideEnd.Value;
                        if (Math.Abs(le.x - subStart.x) > 1e-7 || Math.Abs(le.y - subStart.y) > 1e-7)
                        {
                            output.Add(new LineSeg(le.x, le.y, subStart.x, subStart.y));
                        }
                    }

                    output.Add(sub);
                    lastInsideEnd = subEnd;
                    firstInsideStart ??= subStart;
                }
            }

            // Close the loop with a final connector along the edge if needed.
            if (lastInsideEnd.HasValue && firstInsideStart.HasValue)
            {
                var le = lastInsideEnd.Value;
                var fs = firstInsideStart.Value;
                if (Math.Abs(le.x - fs.x) > 1e-7 || Math.Abs(le.y - fs.y) > 1e-7)
                {
                    output.Add(new LineSeg(le.x, le.y, fs.x, fs.y));
                }
            }

            return output;
        }

        private static bool IsInsideEdge(double x, double y, int edgeType, Domain d) => edgeType switch
        {
            LEFT => x >= d.RInner - 1e-9,
            RIGHT => x <= d.ROuter + 1e-9,
            BOTTOM => y >= d.ZLower - 1e-9,
            TOP => y <= d.ZUpper + 1e-9,
            _ => true
        };

        /// <summary>
        /// Convert a <see cref="GeomLineLoop"/> to an oriented list of <see cref="Seg"/>s
        /// (lines AND arcs), respecting the loop's traversal direction so each segment's
        /// Start/End reflect the order it is visited.
        /// </summary>
        private static List<Seg> LoopToSegs(GeomLineLoop loop)
        {
            var result = new List<Seg>(loop.Boundary.Count);
            if (loop.Boundary.Count == 0) return result;

            static GeomPoint? A(GeomEntity e) => e switch { GeomLine l => l.pt1, GeomArc a => a.StartPt, _ => null };
            static GeomPoint? B(GeomEntity e) => e switch { GeomLine l => l.pt2, GeomArc a => a.EndPt, _ => null };

            // Seed `last` so the first segment can be oriented by what follows it.
            GeomPoint? last = null;
            if (loop.Boundary.Count >= 2)
            {
                var first = loop.Boundary[0];
                var second = loop.Boundary[1];
                var fa = A(first); var fb = B(first);
                var sa = A(second); var sb = B(second);
                if (fb != null && (ReferenceEquals(fb, sa) || ReferenceEquals(fb, sb)))
                    last = fa;   // first segment's start is fa, so traversal will go fa -> fb
                else
                    last = fb;   // first segment is reversed
            }

            foreach (var entity in loop.Boundary)
            {
                var a = A(entity); var b = B(entity);
                if (a == null || b == null) continue;

                bool reversed;
                if (last == null) reversed = false;
                else if (ReferenceEquals(a, last)) reversed = false;
                else if (ReferenceEquals(b, last)) reversed = true;
                else reversed = false;

                var (s, e) = reversed ? (b, a) : (a, b);

                if (entity is GeomLine)
                {
                    result.Add(new LineSeg(s.x, s.y, e.x, e.y));
                }
                else if (entity is GeomArc arc)
                {
                    var center = arc.Center;
                    double startAngle = Math.Atan2(s.y - center.y, s.x - center.x);
                    double sweep = reversed ? -arc.SweepAngle : arc.SweepAngle;
                    result.Add(new ArcSeg(center.x, center.y, arc.Radius, startAngle, sweep));
                }

                last = e;
            }

            return result;
        }

        /// <summary>
        /// Clip entire geometry to domain, creating a new clipped Geometry instance.
        /// </summary>
        public static Geometry ClipGeometryToDomain(Geometry source, Domain domain)
        {
            var clipped = new Geometry(source.PointTolerance);

            // Clip all surfaces
            foreach (var surface in source.Surfaces)
            {
                var clippedBoundary = ClipLineLoop(surface.Boundary, domain, clipped);
                if (clippedBoundary == null)
                    continue; // Surface entirely outside domain

                var clippedHoles = new List<GeomLineLoop>();
                foreach (var hole in surface.Holes)
                {
                    var clippedHole = ClipLineLoop(hole, domain, clipped);
                    if (clippedHole != null)
                        clippedHoles.Add(clippedHole);
                }

                clipped.AddSurface(clippedBoundary, clippedHoles.ToArray());
            }

            return clipped;
        }
    }
}
