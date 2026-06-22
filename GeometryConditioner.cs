using GeometryLib;
using System;
using System.Collections.Generic;
using System.Linq;

namespace electrostat
{
    /// <summary>
    /// Pure mesh-topology conditioning algorithms that operate on a <see cref="Geometry"/>
    /// after components have been clipped to the domain. These have no dependency on the
    /// electrostatic problem assembly (materials / regions / BCs); they exist solely to turn
    /// a clipped, possibly-overlapping set of loops into a topologically consistent boundary
    /// that gmsh's constrained Delaunay mesher (and downstream MFEM) will accept.
    ///
    /// They are ordering-sensitive. The intended pipeline after clipping is:
    /// <see cref="SplitLinesAtIncidentPoints"/> -> <see cref="DropMergedHoleLoops"/> ->
    /// <see cref="PruneUnreferencedEntities"/>.
    /// </summary>
    public static class GeometryConditioner
    {
        /// <summary>
        /// True if the entity is a straight line segment that lies entirely on one of the
        /// four truncated-domain edges (i.e., a "cut" introduced by clipping rather than a
        /// real conductor / material interface). Such segments must NOT inherit the
        /// conductor's Dirichlet tag, otherwise they collide with the domain's own BC at
        /// shared DOFs.
        /// </summary>
        public static bool IsOnDomainEdge(GeomEntity ent, Domain domain)
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
        public static void PruneUnreferencedEntities(Geometry geom)
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
        public static void SplitLinesAtIncidentPoints(Geometry geom)
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
        public static void DropMergedHoleLoops(Geometry geom)
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

        /// <summary>
        /// In an adjacent-phase build the oil surface can list two (or more) distinct
        /// material hole loops that share edge instances (e.g. an interphase pressboard
        /// barrier abutting an interphase angle ring). Each material region meshes the
        /// shared edge as a 2-triangle interface, but the oil region — seeing the same
        /// edge on two independent holes — adds a third oil-side triangle, so gmsh emits a
        /// non-manifold interior edge that MFEM rejects with "Invalid mesh topology.
        /// Interior edge found between 2D elements ...".
        ///
        /// This pass groups holes that touch along shared edge instances and replaces each
        /// group with a single merged hole tracing only the union perimeter (edges used by
        /// exactly one hole in the group). Shared interface edges are dropped from the oil
        /// hole but left on each material surface's own boundary, so those regions still
        /// mesh and share the edge manifold-ly. Only the surface's <see cref="GeomSurface.Holes"/>
        /// list is rewritten; the material boundary loop instances are never mutated.
        ///
        /// Run AFTER <see cref="SplitLinesAtIncidentPoints"/> (so abutting holes share the
        /// same edge instances) and BEFORE <see cref="PruneUnreferencedEntities"/>.
        /// </summary>
        public static void MergeAbuttingHoleLoops(Geometry geom)
        {
            static IEnumerable<GeomPoint> Endpoints(GeomEntity e) => e switch
            {
                GeomLine l => new[] { l.pt1, l.pt2 },
                GeomArc a => new[] { a.StartPt, a.EndPt },
                _ => Array.Empty<GeomPoint>(),
            };

            foreach (var surface in geom.Surfaces)
            {
                if (surface.Holes.Count < 2) continue;

                // edge instance -> holes that reference it. After SplitLinesAtIncidentPoints,
                // abutting holes share the SAME line/arc instance along a common face, so a
                // shared edge shows up in two (or more) holes' lists.
                var holesOfEdge = new Dictionary<GeomEntity, List<GeomLineLoop>>(ReferenceEqualityComparer.Instance);
                foreach (var hole in surface.Holes)
                {
                    if (hole == null) continue;
                    foreach (var e in hole.Boundary)
                    {
                        if (!holesOfEdge.TryGetValue(e, out var list))
                            holesOfEdge[e] = list = new List<GeomLineLoop>();
                        list.Add(hole);
                    }
                }

                // Build an adjacency graph over holes that share at least one edge instance.
                var adjacency = new Dictionary<GeomLineLoop, HashSet<GeomLineLoop>>(ReferenceEqualityComparer.Instance);
                foreach (var hole in surface.Holes)
                    if (hole != null) adjacency[hole] = new HashSet<GeomLineLoop>(ReferenceEqualityComparer.Instance);

                foreach (var holes in holesOfEdge.Values)
                {
                    if (holes.Count < 2) continue;
                    for (int i = 0; i < holes.Count; i++)
                        for (int j = i + 1; j < holes.Count; j++)
                        {
                            if (ReferenceEquals(holes[i], holes[j])) continue;
                            adjacency[holes[i]].Add(holes[j]);
                            adjacency[holes[j]].Add(holes[i]);
                        }
                }

                // Walk connected components over the abutment graph; merge each multi-hole
                // component into a single union hole.
                var visited = new HashSet<GeomLineLoop>(ReferenceEqualityComparer.Instance);
                var newHoles = new List<GeomLineLoop>(surface.Holes.Count);
                bool anyMerged = false;

                foreach (var hole in surface.Holes)
                {
                    if (hole == null || visited.Contains(hole)) continue;

                    var group = new List<GeomLineLoop>();
                    var stack = new Stack<GeomLineLoop>();
                    stack.Push(hole);
                    visited.Add(hole);
                    while (stack.Count > 0)
                    {
                        var h = stack.Pop();
                        group.Add(h);
                        foreach (var nb in adjacency[h])
                            if (visited.Add(nb)) stack.Push(nb);
                    }

                    if (group.Count < 2)
                    {
                        newHoles.AddRange(group);
                        continue;
                    }

                    // Perimeter = edges used by exactly one hole in the group. Shared
                    // interface edges (count >= 2) are interior to the union and are dropped
                    // from the oil hole (they remain on each material surface's boundary).
                    var edgeCount = new Dictionary<GeomEntity, int>(ReferenceEqualityComparer.Instance);
                    foreach (var h in group)
                        foreach (var e in h.Boundary)
                        {
                            edgeCount.TryGetValue(e, out int c);
                            edgeCount[e] = c + 1;
                        }
                    var perimeter = edgeCount.Where(kv => kv.Value == 1).Select(kv => kv.Key).ToList();

                    var cycles = OrderIntoLoops(perimeter, Endpoints);
                    if (cycles == null)
                    {
                        // Could not form clean closed cycles — leave the group as separate
                        // holes rather than emit a discontinuous loop (GmshFile would throw).
                        Console.WriteLine("Warning: could not merge abutting oil hole loops; leaving them separate.");
                        newHoles.AddRange(group);
                        continue;
                    }

                    foreach (var cycle in cycles)
                        newHoles.Add(geom.AddLineLoop(cycle.ToArray()));
                    anyMerged = true;
                }

                if (anyMerged)
                {
                    surface.Holes.Clear();
                    surface.Holes.AddRange(newHoles);
                }
            }
        }

        /// <summary>
        /// Order a flat set of edges into one or more closed loops where each successive
        /// edge shares an endpoint (by reference) with the previous one. Returns null if the
        /// edges cannot be partitioned into closed cycles (any vertex of degree != 2, or an
        /// open chain), in which case the caller must not build a loop from them.
        /// </summary>
        private static List<List<GeomEntity>>? OrderIntoLoops(
            List<GeomEntity> edges,
            Func<GeomEntity, IEnumerable<GeomPoint>> endpoints)
        {
            if (edges.Count == 0) return null;

            var incident = new Dictionary<GeomPoint, List<GeomEntity>>(ReferenceEqualityComparer.Instance);
            foreach (var e in edges)
                foreach (var p in endpoints(e))
                {
                    if (!incident.TryGetValue(p, out var list))
                        incident[p] = list = new List<GeomEntity>();
                    list.Add(e);
                }

            // Disjoint simple cycles require every vertex to have degree exactly 2.
            foreach (var kv in incident)
                if (kv.Value.Count != 2) return null;

            var remaining = new HashSet<GeomEntity>(edges, ReferenceEqualityComparer.Instance);
            var loops = new List<List<GeomEntity>>();

            while (remaining.Count > 0)
            {
                var start = remaining.First();
                remaining.Remove(start);
                var cycle = new List<GeomEntity> { start };

                var ends = endpoints(start).ToArray();
                if (ends.Length != 2) return null;
                GeomPoint startPt = ends[0];
                GeomPoint frontier = ends[1];

                while (!ReferenceEquals(frontier, startPt))
                {
                    GeomEntity? next = null;
                    foreach (var cand in incident[frontier])
                        if (remaining.Contains(cand)) { next = cand; break; }
                    if (next == null) return null; // open chain — cannot close

                    remaining.Remove(next);
                    cycle.Add(next);

                    var ne = endpoints(next).ToArray();
                    frontier = ReferenceEquals(ne[0], frontier) ? ne[1] : ne[0];
                }

                loops.Add(cycle);
            }

            return loops;
        }
    }
}
