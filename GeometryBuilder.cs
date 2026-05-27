using GeometryLib;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Net.Sockets;
using System.Text;
using System.Xml.Linq;
using TfmrLib;
using TfmrLib.FEM;

namespace electrostat
{
    public static class GeometryBuilder
    {
        public static Geometry geometry = new();
        public static FEMProblem? problem = new();

        /// <summary>
        /// Create rectangle for the winding block and fillet only the top-left and top-right corners.
        /// Returns the surface tag (pre-boolean).
        /// </summary>
        public static GeomSurface TopFilletBlock(WindingBlock b)
        {
            return geometry.AddRectWithCornerRadii(b.R0, b.ZBottom, b.Width, b.Height, b.FilletR, b.FilletR, 0.0, 0.0);
        }

        /// <summary>
        /// Build a pressboard barrier, optionally with tapered ends.
        /// </summary>
        public static GeomSurface AddPressboardBarrier(PressboardBarrier pb)
        {
            double r0 = pb.R0;
            double z0 = pb.ZBottom;
            double t = pb.Thickness;
            double h = pb.Height;

            // If no tapers, just return a rectangle
            if (pb.TaperTop == null && pb.TaperBottom == null)
            {
                var pb_bdry = geometry.AddRectangle(r0 + t / 2.0, z0 + h / 2.0, h, t);
                return geometry.AddSurface(pb_bdry);
            }

            // Build the polygon manually
            var points = new List<(double r, double z)>();

            // Bottom edge handling
            if (pb.TaperBottom == null)
            {
                points.Add((r0, z0));
                points.Add((r0 + t, z0));
            }
            else
            {
                var tb = pb.TaperBottom.Value;
                double tEnd = tb.EndThickness;
                double L = tb.Length;
                if (tb.Side == "inner")
                {
                    double rBL = r0 + (t - tEnd);
                    points.Add((rBL, z0));
                    points.Add((r0, z0 + L));
                }
                else // "outer"
                {
                    points.Add((r0, z0));
                }
            }

            // Right (outer) edge - going up
            if (pb.TaperBottom != null && pb.TaperBottom.Value.Side == "outer")
            {
                var tb = pb.TaperBottom.Value;
                double rBR = r0 + tb.EndThickness;
                points.Add((rBR, z0));
                points.Add((r0 + t, z0 + tb.Length));
            }

            if (pb.TaperTop != null && pb.TaperTop.Value.Side == "outer")
            {
                var tt = pb.TaperTop.Value;
                double zTop = z0 + h;
                points.Add((r0 + t, zTop - tt.Length));
                points.Add((r0 + tt.EndThickness, zTop));
            }
            else
            {
                points.Add((r0 + t, z0 + h));
            }

            // Top edge - going left
            if (pb.TaperTop != null && pb.TaperTop.Value.Side == "inner")
            {
                var tt = pb.TaperTop.Value;
                double zTop = z0 + h;
                double rTL = r0 + (t - tt.EndThickness);
                points.Add((rTL, zTop));
                points.Add((r0, zTop - tt.Length));
            }
            else
            {
                points.Add((r0, z0 + h));
            }

            // Remove duplicates
            var filtered = new List<(double r, double z)> { points[0] };
            for (int i = 1; i < points.Count; i++)
            {
                var p = points[i];
                var last = filtered[^1];
                if (Math.Abs(p.r - last.r) > 1e-9 || Math.Abs(p.z - last.z) > 1e-9)
                {
                    filtered.Add(p);
                }
            }
            points = filtered;

            // Create GMSH points
            var pts = points.Select(p => geometry.AddPoint(p.r, p.z)).ToArray();

            // Create lines connecting consecutive points
            var curves = new List<GeomLine>();
            int n = pts.Length;
            for (int i = 0; i < n; i++)
            {
                curves.Add(geometry.AddLine(pts[i], pts[(i + 1) % n]));
            }

            GeomLineLoop loop = geometry.AddLineLoop(curves.ToArray());
            GeomSurface s =geometry.AddSurface(loop);
            return s;
        }

        /// <summary>
        /// Build an L-shaped angle ring with rounded corners, respecting orientation signs.
        /// Optionally includes a taper at the tip of the vertical leg.
        /// </summary>
        public static GeomSurface AddAngleRing(AngleRing ar)
        {
            double r0 = ar.R0;
            double z0 = ar.ZCorner;
            double tv = ar.Tv;
            double th = ar.Th;
            double ri = ar.InsideFilletR;

            // Orientation signs
            double Sr = Math.Sign(ar.Wh);
            double Sz = Math.Sign(ar.Hv);

            double W = Math.Abs(ar.Wh);
            double H = Math.Abs(ar.Hv);
            double Tv = tv;
            double Th = th;

            // Center of curvature for the corners
            double Cx = r0 + Sr * (Tv + ri);
            double Cy = z0 + Sz * (Th + ri);

            // Handle vertical leg tip taper
            var taper = ar.TaperVTip;

            // Points for the vertical leg tip region
            double zTip = z0 + Sz * H;

            var curves = new List<GeomEntity>();
            var tipCurves = new List<GeomLine>();
            GeomPoint p1, p8;
            GeomLine? innerTaperCurve = null;
            GeomLine? outerTaperCurve = null;

            if (taper == null)
            {
                p1 = geometry.AddPoint(r0 + Sr * Tv, zTip);
                p8 = geometry.AddPoint(r0, zTip);
                tipCurves.Add(geometry.AddLine(p1, p8));
            }
            else
            {
                double L = taper.Value.Length;
                double tEnd = taper.Value.EndThickness;
                double zTaperStart = zTip - Sz * L;

                if (taper.Value.Side == "inner")
                {
                    double rInnerTip = r0 + Sr * tEnd;
                    double rInnerTrans = r0 + Sr * Tv;

                    GeomPoint p1Tip = geometry.AddPoint(rInnerTip, zTip);
                    GeomPoint p1Trans = geometry.AddPoint(rInnerTrans, zTaperStart);
                    p8 = geometry.AddPoint(r0, zTip);

                    p1 = p1Trans;
                    tipCurves.Add(geometry.AddLine(p1Tip, p8));
                    innerTaperCurve = geometry.AddLine(p1Trans, p1Tip);
                }
                else // "outer"
                {
                    double rOuterTip = r0 + Sr * (Tv - tEnd);
                    double rOuterTrans = r0;

                    p1 = geometry.AddPoint(r0 + Sr * Tv, zTip);
                    GeomPoint p8Tip = geometry.AddPoint(rOuterTip, zTip);
                    GeomPoint p8Trans = geometry.AddPoint(rOuterTrans, zTaperStart);

                    p8 = p8Trans;
                    tipCurves.Add(geometry.AddLine(p1, p8Tip));
                    outerTaperCurve = geometry.AddLine(p8Tip, p8Trans);
                }
            }

            // 2. Vertical Leg Tangent (Inner)
            GeomPoint p2 = geometry.AddPoint(r0 + Sr * Tv, Cy);

            // Center point for fillet arcs
            GeomPoint pc = geometry.AddPoint(Cx, Cy);

            // 3. Horizontal Leg Tangent (Inner)
            GeomPoint p3 = geometry.AddPoint(Cx, z0 + Sz * Th);

            // 4. Horizontal Leg Tip (Inner)
            GeomPoint p4 = geometry.AddPoint(r0 + Sr * W, z0 + Sz * Th);

            // 5. Horizontal Leg Tip (Outer)
            GeomPoint p5 = geometry.AddPoint(r0 + Sr * W, z0);

            // 6. Horizontal Leg Tangent (Outer)
            GeomPoint p6 = geometry.AddPoint(Cx, z0);

            // 7. Vertical Leg Tangent (Outer)
            GeomPoint p7 = geometry.AddPoint(r0, Cy, 0);

            // Build curves CCW around the L-shape
            // Tip of Vertical Leg
            curves.AddRange(tipCurves);

            if (taper != null && taper.Value.Side == "outer" && outerTaperCurve is not null)
            {
                curves.Add(outerTaperCurve);
            }

            // Outer Vertical Edge (p8 to p7)
            curves.Add(geometry.AddLine(p8, p7));

            // Outer Corner Arc (p7 to p6)
            curves.Add(geometry.AddCircleArc(p7, pc, p6));

            // Outer Horizontal Edge (p6 to p5)
            curves.Add(geometry.AddLine(p6, p5));

            // Tip of Horizontal Leg
            curves.Add(geometry.AddLine(p5, p4));

            // Inner Horizontal Edge (p4 to p3)
            curves.Add(geometry.AddLine(p4, p3));

            // Inner Corner Arc (p3 to p2)
            curves.Add(geometry.AddCircleArc(p3, pc, p2));

            // Inner Vertical Edge (p2 to p1)
            if (taper != null && taper.Value.Side == "inner" && innerTaperCurve is not null)
            {
                curves.Add(geometry.AddLine(p2, p1));
                curves.Add(innerTaperCurve);
            }
            else
            {
                curves.Add(geometry.AddLine(p2, p1));
            }

            GeomLineLoop loop = geometry.AddLineLoop(curves.ToArray());
            GeomSurface s = geometry.AddSurface(loop);

            return s;
        }

        /// <summary>
        /// Returns (metal_surface_tag, paper_surface_tags)
        /// </summary>
        public static (GeomSurface metal, GeomSurface paper) AddStaticRing(StaticRing sr)
        {
            double z0M = sr.ZBottom;

            GeomSurface metal = geometry.AddRectWithCornerRadii(
                r0: sr.R0, z0: z0M, w: sr.Width, h: sr.Height,
                rTL: sr.RTL, rTR: sr.RTR, rBR: sr.RBR, rBL: sr.RBL
            );

            double t = sr.TPaper;
            double r0P = sr.R0 - t;
            double z0P = z0M - t;
            double wP = sr.Width + 2 * t;
            double hP = sr.Height + 2 * t;

            GeomSurface paper = geometry.AddRectWithCornerRadii(
                r0: r0P, z0: z0P, w: wP, h: hP,
                rTL: sr.RTL + t, rTR: sr.RTR + t, rBR: sr.RBR + t, rBL: sr.RBL + t
            );

            paper.Holes.Add(metal.Boundary);

            return (metal, paper);
        }

        public static List<int> Uniq(IEnumerable<int> seq)
        {
            return seq.Distinct().OrderBy(x => x).ToList();
        }

        /// <summary>
        /// True if the entity is a straight line segment that lies entirely on one of the
        /// four truncated-domain edges (i.e., a "cut" introduced by clipping rather than a
        /// real conductor / material interface). Such segments must NOT inherit the
        /// conductor's Dirichlet tag, otherwise they collide with the domain's own BC at
        /// shared DOFs.
        /// </summary>
        private static bool IsOnDomainEdge(GeomEntity ent, Domain domain)
        {
            if (ent is not GeomLine line) return false; // arcs cannot lie on a straight edge
            const double tol = 1e-7;
            // Horizontal edges: both endpoints at z = ZLower or z = ZUpper
            bool onLower = Math.Abs(line.pt1.y - domain.ZLower) < tol
                        && Math.Abs(line.pt2.y - domain.ZLower) < tol;
            bool onUpper = Math.Abs(line.pt1.y - domain.ZUpper) < tol
                        && Math.Abs(line.pt2.y - domain.ZUpper) < tol;
            // Vertical edges: both endpoints at r = RInner or r = ROuter
            bool onInner = Math.Abs(line.pt1.x - domain.RInner) < tol
                        && Math.Abs(line.pt2.x - domain.RInner) < tol;
            bool onOuter = Math.Abs(line.pt1.x - domain.ROuter) < tol
                        && Math.Abs(line.pt2.x - domain.ROuter) < tol;
            return onLower || onUpper || onInner || onOuter;
        }

        /// <summary>
        /// Remove line loops, lines, and arcs from the geometry that are no longer referenced
        /// by any surface (as boundary or hole). Necessary after clipping so gmsh does not emit
        /// the original unclipped primitives that lie outside the domain.
        /// Points are intentionally left in place (they are harmless extras in gmsh output).
        /// </summary>
        private static void PruneUnreferencedEntities(Geometry geom)
        {
            // Collect every line loop that is still referenced by a surface.
            var keepLoops = new HashSet<GeomLineLoop>(ReferenceEqualityComparer.Instance);
            foreach (var surface in geom.Surfaces)
            {
                if (surface.Boundary != null)
                    keepLoops.Add(surface.Boundary);
                foreach (var hole in surface.Holes)
                {
                    if (hole != null)
                        keepLoops.Add(hole);
                }
            }

            // First, strip degenerate edges (zero-length lines / arcs whose start == end)
            // from every surviving loop. Without this, gmsh emits self-loop edges and
            // reports warnings like "Impossible to recover edge N N".
            static bool IsDegenerateLine(GeomLine l) => ReferenceEquals(l.pt1, l.pt2);
            static bool IsDegenerateArc(GeomArc a) => ReferenceEquals(a.StartPt, a.EndPt);

            foreach (var loop in keepLoops)
            {
                loop.Boundary.RemoveAll(e =>
                    (e is GeomLine gl && IsDegenerateLine(gl)) ||
                    (e is GeomArc ga && IsDegenerateArc(ga)));
            }

            // Collect every line / arc reachable from those loops.
            var keepLines = new HashSet<GeomLine>(ReferenceEqualityComparer.Instance);
            var keepArcs = new HashSet<GeomArc>(ReferenceEqualityComparer.Instance);
            foreach (var loop in keepLoops)
            {
                foreach (var entity in loop.Boundary)
                {
                    if (entity is GeomLine line) keepLines.Add(line);
                    else if (entity is GeomArc arc) keepArcs.Add(arc);
                }
            }

            geom.LineLoops.RemoveAll(l => !keepLoops.Contains(l));

            // Use RemoveLine / RemoveArc rather than RemoveAll directly so the dedup caches
            // in `Geometry` are kept in sync. Snapshot first to avoid mutating during iteration.
            foreach (var l in geom.Lines.ToList())
            {
                if (!keepLines.Contains(l) || IsDegenerateLine(l))
                    geom.RemoveLine(l);
            }
            foreach (var a in geom.Arcs.ToList())
            {
                if (!keepArcs.Contains(a) || IsDegenerateArc(a))
                    geom.RemoveArc(a);
            }

            // Collect every point reachable from the surviving lines and arcs.
            var keepPoints = new HashSet<GeomPoint>(ReferenceEqualityComparer.Instance);
            foreach (var line in geom.Lines)
            {
                if (line.pt1 != null) keepPoints.Add(line.pt1);
                if (line.pt2 != null) keepPoints.Add(line.pt2);
            }
            foreach (var arc in geom.Arcs)
            {
                if (arc.StartPt != null) keepPoints.Add(arc.StartPt);
                if (arc.EndPt != null) keepPoints.Add(arc.EndPt);
            }

            geom.Points.RemoveWhere(p => !keepPoints.Contains(p));
        }

        /// <summary>
        /// For every surviving <see cref="GeomLine"/>, finds any <see cref="GeomPoint"/> lying on
        /// the line's interior (within the geometry's point tolerance) and splits the line at
        /// those points. Each containing <see cref="GeomLineLoop"/> has the original line replaced
        /// in‑place with the resulting sub‑line sequence, preserving traversal direction.
        ///
        /// Why this is needed: gmsh constrained Delaunay cannot insert two boundary edges that
        /// overlap geometrically without sharing endpoints. After splitting, both loops that
        /// happen to lie on the same line segment share the *same* sub‑line instance (because
        /// <see cref="Geometry.AddLine"/> deduplicates), giving gmsh a conforming boundary.
        ///
        /// Only lines are split here — arcs are left alone.
        /// </summary>
        private static void SplitLinesAtIncidentPoints(Geometry geom)
        {
            static (GeomPoint? start, GeomPoint? end) GetEndpoints(GeomEntity e) => e switch
            {
                GeomLine l => (l.pt1, l.pt2),
                GeomArc a => (a.StartPt, a.EndPt),
                _ => (null, null),
            };

            static bool DetermineForward(GeomLineLoop loop, int idx, GeomLine line)
            {
                int n = loop.Boundary.Count;
                if (n <= 1) return true;

                // Prefer the previous entity: its exit point is the entry point of `line`.
                var prev = loop.Boundary[(idx - 1 + n) % n];
                var (ps, pe) = GetEndpoints(prev);
                if (ReferenceEquals(ps, line.pt1) || ReferenceEquals(pe, line.pt1)) return true;
                if (ReferenceEquals(ps, line.pt2) || ReferenceEquals(pe, line.pt2)) return false;

                // Fall back to the next entity: its entry point is the exit point of `line`.
                var next = loop.Boundary[(idx + 1) % n];
                var (ns, ne) = GetEndpoints(next);
                if (ReferenceEquals(ns, line.pt2) || ReferenceEquals(ne, line.pt2)) return true;
                if (ReferenceEquals(ns, line.pt1) || ReferenceEquals(ne, line.pt1)) return false;

                return true;
            }

            double tol = geom.PointTolerance;

            // Iterate until no more splits happen. Each iteration strictly reduces the set of
            // lines that still have interior points (sub‑lines are shorter than their parent),
            // so this converges; the safety cap is just defensive.
            int safety = 0;
            bool changed = true;
            while (changed && safety++ < 32)
            {
                changed = false;

                // Snapshot the line list — we mutate geom.Lines during the loop.
                var linesToCheck = geom.Lines.ToList();

                foreach (var line in linesToCheck)
                {
                    double dx = line.pt2.x - line.pt1.x;
                    double dy = line.pt2.y - line.pt1.y;
                    double lenSq = dx * dx + dy * dy;
                    if (lenSq <= tol * tol) continue; // degenerate; already filtered, but be safe

                    // Find every point that lies strictly on the interior of this segment.
                    var hits = new List<(GeomPoint pt, double t)>();
                    foreach (var p in geom.Points)
                    {
                        if (ReferenceEquals(p, line.pt1) || ReferenceEquals(p, line.pt2)) continue;

                        double vx = p.x - line.pt1.x;
                        double vy = p.y - line.pt1.y;
                        double t = (vx * dx + vy * dy) / lenSq;
                        if (t <= 1e-12 || t >= 1.0 - 1e-12) continue;

                        // Perpendicular distance to the line.
                        double projX = line.pt1.x + t * dx;
                        double projY = line.pt1.y + t * dy;
                        double pdx = p.x - projX;
                        double pdy = p.y - projY;
                        if ((pdx * pdx + pdy * pdy) > tol * tol) continue;

                        hits.Add((p, t));
                    }

                    if (hits.Count == 0) continue;

                    // Sort by parametric position along the line so the resulting sub‑lines
                    // are emitted in geometric order from pt1 to pt2.
                    hits.Sort((a, b) => a.t.CompareTo(b.t));

                    // Build the natural (forward) sub‑line sequence: pt1 -> p1 -> p2 -> ... -> pt2.
                    // AddLine deduplicates by point ID pair, so coincident edges from other loops
                    // will collapse onto these sub‑lines automatically.
                    var sequence = new List<GeomLine>(hits.Count + 1);
                    GeomPoint prevPt = line.pt1;
                    foreach (var (p, _) in hits)
                    {
                        if (ReferenceEquals(prevPt, p)) continue; // shouldn't happen, but safe
                        sequence.Add(geom.AddLine(prevPt, p));
                        prevPt = p;
                    }
                    if (!ReferenceEquals(prevPt, line.pt2))
                        sequence.Add(geom.AddLine(prevPt, line.pt2));

                    if (sequence.Count == 0) continue;

                    // Propagate the parent's Tag (e.g., a domain-edge "Lower_Bdry" tag) onto
                    // every sub-line. Without this, splitting the bottom domain edge into
                    // pieces would erase its physical-curve tag because the new sub-line
                    // instances default to Tag=0 and the original line is about to be removed.
                    if (line.Tag > 0)
                    {
                        foreach (var sub in sequence)
                        {
                            if (sub.Tag == 0) sub.Tag = line.Tag;
                        }
                    }

                    // Replace the line in every loop that referenced it, preserving direction.
                    foreach (var loop in geom.LineLoops)
                    {
                        for (int i = 0; i < loop.Boundary.Count; i++)
                        {
                            if (!ReferenceEquals(loop.Boundary[i], line)) continue;

                            bool forward = DetermineForward(loop, i, line);
                            IEnumerable<GeomEntity> sub = forward ? sequence : Enumerable.Reverse(sequence);

                            loop.Boundary.RemoveAt(i);
                            loop.Boundary.InsertRange(i, sub);

                            // Skip past the inserted sub‑lines; none of them are the original `line`.
                            i += sequence.Count - 1;
                        }
                    }

                    // Drop the original line; the new sub‑lines have replaced it everywhere.
                    // Use RemoveLine so the deduplication cache is also evicted — otherwise a
                    // later AddLine(pt1, pt2) call would hand back this dead instance and the
                    // gmsh writer would fail to find a matching GmshLine for it.
                    geom.RemoveLine(line);
                    changed = true;
                }
            }
        }

        /// <summary>
        /// After clipping, an electrode that has been partially truncated may end up with its
        /// hole loop sharing one or more edges with the enclosing surface's outer boundary
        /// (typical case: a winding clipped to the bottom of the domain shares its bottom edge
        /// with <c>bottom_bdry</c>). In that situation the loop is no longer a true hole —
        /// the "interior" region is connected to the exterior through the shared edge —
        /// and gmsh cannot tessellate around it: it inserts boundary nodes along the shared
        /// edge but generates no incident triangles, producing orphan nodes that MFEM reports
        /// as <c>be_to_face[..] == -1</c>.
        ///
        /// This method drops any hole loop from each surface that shares at least one edge
        /// instance (line or arc, by reference) with that surface's outer boundary.
        ///
        /// Important: a hole loop must NOT be dropped if it is still the outer boundary of
        /// another surviving surface (e.g. a pressboard / angle-ring / static-ring-paper
        /// region whose interior is meshed with its own material attribute). Dropping such
        /// a hole would leave both the enclosing surface (oil) and the component surface
        /// tessellating the same region — gmsh emits overlapping triangles and MFEM's
        /// Mesh::Finalize aborts with "Boundary elements with wrong orientation … Interior
        /// face with incompatible orientations". The hole must only be removed when the
        /// component surface itself has already been dropped from <c>geom.Surfaces</c>
        /// (this is the case for electrode interiors — windings, static-ring metals — which
        /// participate only as Dirichlet boundaries and whose surface was removed earlier).
        /// </summary>
        private static void DropMergedHoleLoops(Geometry geom)
        {
            static IEnumerable<GeomPoint> Endpoints(GeomEntity e) => e switch
            {
                GeomLine l => new[] { l.pt1, l.pt2 },
                GeomArc a => new[] { a.StartPt, a.EndPt },
                _ => Array.Empty<GeomPoint>(),
            };

            static bool ShareEndpoint(GeomEntity a, GeomEntity b)
            {
                foreach (var pa in Endpoints(a))
                    foreach (var pb in Endpoints(b))
                        if (ReferenceEquals(pa, pb)) return true;
                return false;
            }

            // Any loop that is currently the outer boundary of some surface must be kept as
            // a hole on its enclosing surface, otherwise the two surfaces will overlap.
            var loopsUsedAsOuter = new HashSet<GeomLineLoop>(ReferenceEqualityComparer.Instance);
            foreach (var s in geom.Surfaces)
            {
                if (s.Boundary != null) loopsUsedAsOuter.Add(s.Boundary);
            }

            foreach (var surface in geom.Surfaces)
            {
                if (surface.Boundary == null || surface.Holes.Count == 0) continue;

                var holesToRemove = new List<GeomLineLoop>();

                foreach (var hole in surface.Holes)
                {
                    if (hole == null) continue;
                    if (loopsUsedAsOuter.Contains(hole)) continue; // material region — keep hole

                    // Rebuild on every iteration: a previous splice will have changed surface.Boundary.Boundary.
                    var outerEdges = new HashSet<GeomEntity>(
                        surface.Boundary.Boundary, ReferenceEqualityComparer.Instance);

                    int n = hole.Boundary.Count;
                    if (n == 0) { holesToRemove.Add(hole); continue; }

                    var shared = new bool[n];
                    int sharedCount = 0;
                    for (int i = 0; i < n; i++)
                    {
                        if (outerEdges.Contains(hole.Boundary[i]))
                        {
                            shared[i] = true;
                            sharedCount++;
                        }
                    }

                    if (sharedCount == 0) continue;            // genuine hole — keep
                    if (sharedCount == n)                      // whole loop coincides with outer; nothing to preserve
                    {
                        holesToRemove.Add(hole);
                        continue;
                    }

                    // Splice is only well-defined when the shared edges form a single
                    // contiguous run around the cyclic hole boundary.
                    int holeTransitions = 0;
                    for (int i = 0; i < n; i++)
                        if (shared[i] && !shared[(i + 1) % n]) holeTransitions++;
                    if (holeTransitions != 1)
                    {
                        Console.WriteLine("Warning: merged hole loop has non-contiguous shared edges; dropping (conductor edges may be lost).");
                        holesToRemove.Add(hole);
                        continue;
                    }

                    // Find the first index of the shared run (cyclically).
                    int sharedStart = -1;
                    for (int i = 0; i < n; i++)
                    {
                        if (shared[i] && !shared[(i + n - 1) % n]) { sharedStart = i; break; }
                    }
                    if (sharedStart < 0) { holesToRemove.Add(hole); continue; }

                    // Collect the non-shared run in hole traversal order, starting right
                    // after the shared run.
                    var unsharedRun = new List<GeomEntity>(n - sharedCount);
                    for (int k = sharedCount; k < n; k++)
                        unsharedRun.Add(hole.Boundary[(sharedStart + k) % n]);

                    // Locate the same shared run inside the outer boundary (by reference).
                    var sharedSet = new HashSet<GeomEntity>(n - unsharedRun.Count, ReferenceEqualityComparer.Instance);
                    for (int k = 0; k < sharedCount; k++)
                        sharedSet.Add(hole.Boundary[(sharedStart + k) % n]);

                    var outerList = surface.Boundary.Boundary;
                    int m = outerList.Count;
                    int outerSharedCount = 0;
                    int outerStart = -1;
                    int outerTransitions = 0;
                    for (int i = 0; i < m; i++)
                    {
                        bool curShared = sharedSet.Contains(outerList[i]);
                        if (curShared) outerSharedCount++;
                        if (curShared && !sharedSet.Contains(outerList[(i - 1 + m) % m])) outerStart = i;
                        if (curShared && !sharedSet.Contains(outerList[(i + 1) % m])) outerTransitions++;
                    }
                    if (outerStart < 0 || outerSharedCount != sharedCount || outerTransitions != 1)
                    {
                        Console.WriteLine("Warning: merged hole shared edges are non-contiguous in outer boundary; dropping (conductor edges may be lost).");
                        holesToRemove.Add(hole);
                        continue;
                    }

                    // Rotate outer so the shared run sits at positions [0, outerSharedCount),
                    // then drop it. The element now at the END of `rotated` is the edge that
                    // immediately precedes the spliced section in cyclic order; the element at
                    // index 0 is the edge that immediately follows it.
                    var rotated = new List<GeomEntity>(m);
                    for (int i = 0; i < m; i++) rotated.Add(outerList[(outerStart + i) % m]);
                    rotated.RemoveRange(0, outerSharedCount);

                    // Decide insertion direction by endpoint adjacency. The hole and outer
                    // traverse the shared edges in opposite senses, but the geom library does
                    // not record per-edge direction explicitly — adjacency in the loop list is
                    // the only invariant we can rely on.
                    IEnumerable<GeomEntity> toInsert = unsharedRun;
                    if (rotated.Count > 0)
                    {
                        var prevEdge = rotated[rotated.Count - 1]; // edge immediately before spliced section
                        if (!ShareEndpoint(prevEdge, unsharedRun[0]))
                        {
                            if (ShareEndpoint(prevEdge, unsharedRun[unsharedRun.Count - 1]))
                            {
                                toInsert = Enumerable.Reverse(unsharedRun);
                            }
                            else
                            {
                                Console.WriteLine("Warning: cannot determine splice direction for merged hole; dropping.");
                                holesToRemove.Add(hole);
                                continue;
                            }
                        }
                    }

                    rotated.InsertRange(0, toInsert);

                    outerList.Clear();
                    outerList.AddRange(rotated);

                    holesToRemove.Add(hole);
                }

                if (holesToRemove.Count > 0)
                {
                    var removeSet = new HashSet<GeomLineLoop>(holesToRemove, ReferenceEqualityComparer.Instance);
                    surface.Holes.RemoveAll(h => removeSet.Contains(h));
                }
            }
        }

        // ----------------------------
        // GetDP Analysis
        // ----------------------------

        public static int RunGetDPAnalysis()
        {
            //problem?.Filename = "getdp/problem.pro";
            problem?.Solve();
            return 0;
        }

        // ----------------------------
        // Build
        // ----------------------------

        /// <summary>
        /// Reset the static geometry to an empty state. Useful when building a
        /// new model (e.g. for visualization) without accumulating prior entities.
        /// </summary>
        public static void ResetGeometry()
        {
            geometry = new Geometry();
        }

        /// <summary>
        /// Build the geometry, mesh it, run the MFEM solver, and return the populated
        /// <see cref="FEMProblem"/> (including <see cref="FEMProblem.Solution"/>) for
        /// visualization.
        /// </summary>
        public static FEMProblem? BuildAndSolve(
            ElectrostatCase _case,
            string caseName,
            double lc = 5.0,
            bool clipToDomain = true)
        {
            ResetGeometry();
            BuildModel(
                _case,
                lc: lc,
                mshOut: $"{caseName}/geom.msh",
                clipToDomain: clipToDomain,
                generateMesh: true);
            RunGetDPAnalysis();
            return problem;
        }

        /// <summary>
        /// Build the geometry without invoking gmsh / writing a mesh file.
        /// Returns the populated <see cref="Geometry"/> for visualization.
        /// </summary>
        public static Geometry BuildGeometryOnly(
            ElectrostatCase _case,
            bool clipToDomain = true)
        {
            ResetGeometry();
            BuildModel(_case, lc: 5.0, mshOut: null, clipToDomain: clipToDomain, generateMesh: false);
            return geometry;
        }

        public static void BuildModel(
            ElectrostatCase _case,
            double lc = 100.0,
            string? mshOut = "msh/geom.msh",
            bool clipToDomain = true,
            bool generateMesh = true)
        {
            var domain = _case.Domain;
            var windings = _case.Windings;
            var pressboards = _case.Pressboards;
            var angleRings = _case.AngleRings;
            var staticRings = _case.StaticRings;
            var voltages = _case.Voltages;

            //Gmsh.Model.Add("axisym_param");
            MeshGenerator gmsh = new MeshGenerator();

            TagManager tags = new TagManager();
   
            if (false)
            {
                problem = new GetDPAxiElecProblem();
            }
            else
            {
                problem = new MFEMProblem();
            }

            var oil = new Material("Oil");
            oil.Properties.Add("epsilon_r", 2.2);
            problem.Materials.Add(oil);
            var paper = new Material("Paper");
            paper.Properties.Add("epsilon_r", 4.4);
            problem.Materials.Add(paper);
            var pressboard = new Material("Pressboard");
            pressboard.Properties.Add("epsilon_r", 4.4);
            problem.Materials.Add(pressboard);

            // Outer computational region (oil box)
            double domain_h = domain.ZUpper - domain.ZLower;
            double domain_w = domain.ROuter - domain.RInner;
            GeomPoint lower_left = geometry.AddPoint(domain.RInner, domain.ZLower);
            GeomPoint upper_left = geometry.AddPoint(domain.RInner, domain.ZUpper);
            GeomPoint upper_right = geometry.AddPoint(domain.ROuter, domain.ZUpper);
            GeomPoint lower_right = geometry.AddPoint(domain.ROuter, domain.ZLower);
            GeomLine left_bdry = geometry.AddLine(lower_left, upper_left);
            GeomLine top_bdry = geometry.AddLine(upper_left, upper_right);
            GeomLine right_bdry = geometry.AddLine(upper_right, lower_right);
            GeomLine bottom_bdry = geometry.AddLine(lower_right, lower_left);
            GeomLineLoop oil_bdry = geometry.AddLineLoop([left_bdry, top_bdry, right_bdry, bottom_bdry]);
            var oil_surf = geometry.AddSurface(oil_bdry);
            int oil_tag = tags.TagEntityByString(oil_surf, "Oil");
            var region = new Region("Oil", [oil_tag], oil);
            problem.Regions.Add(region);
            int leftTag = tags.TagEntityByString(left_bdry, "Core");
            int topTag = tags.TagEntityByString(top_bdry, "TopYoke");
            int rightTag = tags.TagEntityByString(right_bdry, "Tank");
            int bottomTag = tags.TagEntityByString(bottom_bdry, "Lower_Bdry");

            // Domain-wall Dirichlet BCs. Each of the four outer-domain edges was tagged
            // above as its own physical curve; emit a DirichletBoundaryCondition for any
            // wall whose name appears in the case's voltages map. Without this the walls
            // are treated as natural (Neumann) boundaries and the potential floats — this
            // is what was causing the top and right edges to show non-zero V even though
            // the input deck specifies 0 V for "TopYoke" and "Tank".
            void AddWallBC(string name, int tag)
            {
                if (tag <= 0) return;
                if (!voltages.TryGetValue(name, out var v)) return;
                problem.BoundaryConditions.Add(new DirichletBoundaryCondition
                {
                    Name = name,
                    Tags = new List<int> { tag },
                    Potential = v,
                });
            }
            AddWallBC("Core", leftTag);
            AddWallBC("TopYoke", topTag);
            AddWallBC("Tank", rightTag);
            AddWallBC("Lower_Bdry", bottomTag);
            // Loops whose surviving (post-clip, post-split) boundary segments should be
            // refined. Captured here as loops rather than tags so we can resolve them to
            // GeomLine/GeomArc instances after SplitLinesAtIncidentPoints() runs.
            var refineLoops = new List<GeomLineLoop>();
            // Curves we want to refine around
            var refineCurves = new List<int>();

            // Track components for clipping
            var componentSurfaces = new List<(GeomSurface surf, string name, string type)>();
            var electrodes = new List<(GeomSurface surf, string name, string type)>();

            // --- Phase 1: Build FULL geometry (unrestricted by domain) ---
            foreach (var w in windings)
            {
                var wdg_block_surf = TopFilletBlock(w);
                //tags.TagEntityByString(wdg_block_surf, w.Name);
                componentSurfaces.Add((wdg_block_surf, w.Name, "winding"));
                tags.TagEntityByString(wdg_block_surf.Boundary, w.Name + "Bdry");
                electrodes.Add((wdg_block_surf, w.Name, "winding"));
            }

            foreach (var pb in pressboards)
            {
                var pb_surf = AddPressboardBarrier(pb);
                int pb_tag = tags.TagEntityByString(pb_surf, pb.Name);
                componentSurfaces.Add((pb_surf, pb.Name, "pressboard"));
                var pb_region = new Region(pb.Name, [pb_tag], pressboard);
                problem.Regions.Add(pb_region);
            }

            foreach (var ar in angleRings)
            {
                var ar_surf = AddAngleRing(ar);
                int ar_tag = tags.TagEntityByString(ar_surf, ar.Name);
                componentSurfaces.Add((ar_surf, ar.Name, "anglering"));
                var ar_region = new Region(ar.Name, [ar_tag], pressboard);
                problem.Regions.Add(ar_region);
            }

            foreach (var sr in staticRings)
            {
                var (metal_surf, paper_surf) = AddStaticRing(sr);
                //componentSurfaces.Add((metal_surf, sr.Name + "_Metal", "static_metal"));
                //tags.TagEntityByString(metal_surf, sr.Name + "_Metal");
                tags.TagEntityByString(metal_surf.Boundary, sr.Name + "_Metal_Bdry");
                electrodes.Add((metal_surf, sr.Name + "_Metal", "static_metal"));
                int paper_tag = tags.TagEntityByString(paper_surf, sr.Name + "_Paper");
                componentSurfaces.Add((paper_surf, sr.Name + "_Paper", "static_paper"));
                var paper_region = new Region(sr.Name+"_Paper", [paper_tag], paper);
                problem.Regions.Add(paper_region);
            }

            // --- Phase 2: Clip components to domain (if enabled) ---
            int clippedCount = 0;
            int skippedCount = 0;

            // Track surfaces that should be removed from the geometry (fully outside / degenerate).
            var surfacesToRemove = new List<GeomSurface>();

            foreach (var (surf, name, type) in componentSurfaces)
            {
                var bounds = surf.Boundary.GetBoundingBox();

                // Guard against degenerate/invalid geometry
                if (double.IsNaN(bounds.minX) || double.IsNaN(bounds.maxX) ||
                    double.IsNaN(bounds.minY) || double.IsNaN(bounds.MaxY) ||
                    double.IsInfinity(bounds.minX) || double.IsInfinity(bounds.maxX) ||
                    double.IsInfinity(bounds.minY) || double.IsInfinity(bounds.MaxY))
                {
                    Console.WriteLine($"Warning: {name} ({type}) has invalid bounds (NaN/Infinity), skipping");
                    surfacesToRemove.Add(surf);
                    skippedCount++;
                    continue;
                }

                // Check if component intersects domain - note: MaxY with capital M
                if (!domain.Intersects(bounds.minX, bounds.minY, bounds.maxX, bounds.MaxY))
                {
                    Console.WriteLine($"Info: {name} ({type}) is entirely outside domain, removing");
                    surfacesToRemove.Add(surf);
                    skippedCount++;
                    continue;
                }

                GeomLineLoop boundaryToAdd;

                if (clipToDomain)
                {
                    // Check if entirely within domain (fast path - no clipping needed)
                    if (domain.FullyContains(bounds.minX, bounds.minY, bounds.maxX, bounds.MaxY))
                    {
                        boundaryToAdd = surf.Boundary;
                    }
                    else
                    {
                        // Partially outside - clip it
                        Console.WriteLine($"Info: Clipping {name} ({type}) to domain bounds");
                        var clippedBoundary = GeometryClipping.ClipLineLoop(surf.Boundary, domain, geometry);

                        if (clippedBoundary == null)
                        {
                            Console.WriteLine($"Warning: {name} ({type}) became degenerate after clipping, removing");
                            surfacesToRemove.Add(surf);
                            skippedCount++;
                            continue;
                        }

                        // Replace the component surface's boundary with the clipped loop so
                        // the surface itself is the clipped region (and not the original full extent).
                        surf.Boundary = clippedBoundary;
                        boundaryToAdd = clippedBoundary;
                        clippedCount++;
                    }
                }
                else
                {
                    // No clipping - use original boundary
                    boundaryToAdd = surf.Boundary;
                }

                // Add to oil surface holes.
                // NOTE: For a static ring the metal sits inside the paper; the paper boundary
                // is what touches the oil region. The metal boundary is still referenced as a
                // hole in the paper surface, and is still used for the Dirichlet boundary condition
                // below.
                if (type != "static_metal")
                {
                    oil_surf.Holes.Add(boundaryToAdd);
                    refineCurves.Add(boundaryToAdd.Tag);
                    refineLoops.Add(boundaryToAdd);
                }
                else
                {
                    refineCurves.Add(boundaryToAdd.Tag);
                    refineLoops.Add(boundaryToAdd);
                }

                // Add material regions for non-electrodes
                if (type == "pressboard" || type == "anglering")
                {
                    // Note: This creates a region but the surface may need reconstruction
                    // For now, just track the boundary
                }
            }

            // Remove surfaces that are outside the domain or became degenerate.
            foreach (var s in surfacesToRemove)
            {
                geometry.Surfaces.Remove(s);
            }

            // Drop electrode interiors (windings, static-ring metals) from the surface list.
            // These conductors participate only as Dirichlet boundaries on the oil / paper
            // regions: their loops are already added as holes above. If we leave them in
            // `geometry.Surfaces`, the writer emits a standalone `Plane Surface` for each,
            // which gmsh meshes — but msh2 only writes 2D elements for surfaces with a
            // `Physical Surface` tag (electrodes have none). The interior triangles are
            // dropped, while the boundary nodes that gmsh created on the electrode's edges
            // remain in `$Nodes`. The oil-side mesh reuses those parametric edges and so
            // shares the boundary edge nodes — but along the bottom-of-domain truncation
            // segment, the oil side does NOT touch the electrode (it sees the line only as
            // part of its outer Lower_Bdry edge), so those nodes become orphans: present
            // in `$Nodes` and referenced by `Physical Curve (5)` boundary line elements,
            // but not adjacent to any 2D element. MFEM's CheckBdrElementOrientation then
            // dereferences `be_to_face[..] == -1`, aborting in debug and silently
            // corrupting BC assignment in release. Removing the surface stops gmsh from
            // discretising the electrode interior at all, so no orphan nodes are created.
            var electrodeSurfacesToDrop = new HashSet<GeomSurface>(ReferenceEqualityComparer.Instance);
            foreach (var (surf, _, etype) in electrodes)
            {
                if (etype == "winding" || etype == "static_metal")
                    electrodeSurfacesToDrop.Add(surf);
            }
            geometry.Surfaces.RemoveAll(s => electrodeSurfacesToDrop.Contains(s));

            // Capture each electrode's intended Dirichlet tag BEFORE splitting/pruning,
            // then clear the loop-level tag so the GMSH writer does not emit the whole
            // clipped loop as a single physical curve (which would include any segment
            // lying on a truncated domain edge).
            var electrodeLoopTags = new List<(GeomLineLoop loop, int tag)>();
            foreach (var (surf, _, _) in electrodes)
            {
                if (surf?.Boundary == null) continue;
                int loopTag = surf.Boundary.Tag;
                if (loopTag <= 0) continue;
                electrodeLoopTags.Add((surf.Boundary, loopTag));
                surf.Boundary.Tag = 0;
            }

            // Split any line that has another point lying on its interior so that adjacent
            // components which share an edge end up referencing the same sub-line, yielding
            // a conforming boundary for gmsh.
            SplitLinesAtIncidentPoints(geometry);

            // After splitting, an electrode hole loop may now share one or more edge instances
            // with the oil (or paper) outer boundary — e.g. a winding clipped to the bottom of
            // the domain shares its bottom edge with bottom_bdry. Such a "hole" is no longer
            // interior to the enclosing surface and would cause gmsh to insert orphan nodes
            // along the shared edge. Drop those hole loops here so the remaining surface has
            // a topologically consistent boundary.
            DropMergedHoleLoops(geometry);

            // Prune any line loops / lines / arcs that are no longer referenced by any surface.
            // This is what was leaking outside-domain geometry.
            PruneUnreferencedEntities(geometry);

            // Re-apply each electrode's Dirichlet tag to its surviving boundary sub-segments,
            // skipping any segment that lies on a truncated-domain edge. Those truncation
            // segments are NOT real conductor surfaces; tagging them would conflict with
            // the domain-edge BC at shared DOFs (the MFEM "Conflicting Boundary Conditions"
            // error you would otherwise see). We do this AFTER SplitLinesAtIncidentPoints so
            // we tag the actual sub-line instances that end up in the gmsh output.
            foreach (var (loop, tag) in electrodeLoopTags)
            {
                foreach (var ent in loop.Boundary)
                {
                    if (IsOnDomainEdge(ent, domain)) continue;
                    if (ent is GeomLine gl)
                    {
                        // Don't overwrite a non-electrode tag if one was somehow set.
                        if (gl.Tag == 0) gl.Tag = tag;
                    }
                    else if (ent is GeomArc ga)
                    {
                        if (ga.Tag == 0) ga.Tag = tag;
                    }
                }
            }

            if (clipToDomain)
            {
                Console.WriteLine($"Domain clipping summary: {clippedCount} components clipped, {skippedCount} components removed");
            }

            double? potential;
            foreach (var (surf, name, type) in electrodes)
            {
                potential = voltages[name];
                // Add boundary conditions for electrodes.
                // Use the tag we captured before clearing surf.Boundary.Tag above; that is
                // also the per-segment tag now applied to the conductor's surviving sub-edges,
                // which GmshFile aggregates into a single Physical Curve.
                if (potential.HasValue)
                {
                    int bcTag = electrodeLoopTags
                        .Where(t => ReferenceEquals(t.loop, surf.Boundary))
                        .Select(t => t.tag)
                        .DefaultIfEmpty(surf.Boundary.Tag)
                        .First();
                    if (bcTag <= 0) continue;
                    var bc = new DirichletBoundaryCondition
                    {
                        Name = name,
                        Tags = new List<int> { bcTag },
                        Potential = potential.Value
                    };
                    problem.BoundaryConditions.Add(bc);
                }
            }

            refineCurves = Uniq(refineCurves);

            // Ensure output directory exists
            if (generateMesh && !string.IsNullOrEmpty(mshOut))
            {
                string meshDir = Path.GetDirectoryName(mshOut);
                if (string.IsNullOrEmpty(meshDir)) meshDir = ".";
                Directory.CreateDirectory(meshDir);

                // --- Phase 3: Mesh the geometry ---
                gmsh.AddGeometry(geometry);

                // Local refinement around every electrode / pressboard / angle-ring
                // boundary that survived clipping. We feed the actual sub-segments
                // (post-SplitLinesAtIncidentPoints) so the Distance field samples the
                // real curves gmsh will mesh, not their pre-clip parents. Sizes are
                // expressed relative to `lc` so this scales with the caller's request.
                var refineEntities = new List<object>();
                foreach (var loop in refineLoops)
                {
                    if (loop?.Boundary == null) continue;
                    foreach (var ent in loop.Boundary)
                    {
                        // Skip segments that ended up on a truncated domain edge —
                        // they aren't really conductor boundaries.
                        if (IsOnDomainEdge(ent, domain)) continue;
                        refineEntities.Add(ent);
                    }
                }
                if (refineEntities.Count > 0)
                {
                    gmsh.AddDistanceRefinement(
                        curves: refineEntities,
                        sizeMin: Math.Max(0.2, 0.15 * lc),
                        sizeMax: lc,
                        distMin: 0.5 * lc,
                        distMax: 6.0 * lc,
                        sampling: 100);
                }

                // Pin the outer domain boundary (core / yoke / tank / bottom) to a uniform
                // cell size of `lc`. Without this gmsh's 1D mesher is free to subdivide a
                // ~1 m long edge with just a handful of nodes (since we disable
                // MeshSizeFromPoints when the background field is active), and the 2D
                // mesher then has to bridge from finely-spaced conductor edges to those
                // sparse wall nodes — producing the long sliver-triangle fans you see
                // along the tank wall and top yoke. SizeMin == SizeMax means "always lc
                // along this boundary" regardless of distance from it.
                var outerEntities = new List<object>();
                if (oil_surf?.Boundary?.Boundary != null)
                {
                    foreach (var ent in oil_surf.Boundary.Boundary)
                    {
                        outerEntities.Add(ent);
                    }
                }
                if (outerEntities.Count > 0)
                {
                    gmsh.AddDistanceRefinement(
                        curves: outerEntities,
                        sizeMin: lc,
                        sizeMax: lc,
                        distMin: 0.0,
                        distMax: lc,
                        sampling: 50);
                }

                gmsh.GenerateMesh(mshOut, lc);

                problem.MeshFile = mshOut;
            }
            // Export geometry to BREP format
            //string brepOut = mshOut.Replace(".msh", ".brep");
            //if (brepOut == mshOut) brepOut += ".brep";
            //Gmsh.Write(brepOut);
        }

    }

}
