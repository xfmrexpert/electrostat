using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Text;

namespace electrostat
{
    public static class GeometryBuilder
    {
        // ----------------------------
        // Geometry helpers
        // ----------------------------

        /// <summary>
        /// Add a rectangle surface in the r-z plane (z=0 out-of-plane).
        /// </summary>
        public static int AddRect(double r0, double z0, double w, double h)
        {
            return Gmsh.Model.Occ.AddRectangle(r0, z0, 0.0, w, h);
        }

        /// <summary>
        /// Create a rectangle surface and apply independent fillets at each corner.
        /// (r0, z0) is lower-left.
        /// Returns surface tag.
        /// </summary>
        public static int AddRectWithCornerRadii(
            double r0, double z0, double w, double h,
            double rTL, double rTR, double rBR, double rBL)
        {
            double startX = r0 + rBL;
            double startY = z0;

            int currentTag = Gmsh.Model.Occ.AddPoint(startX, startY, 0);
            int startTag = currentTag;

            var loopCurves = new List<int>();

            // --- 1. Bottom Edge: BL Exit -> BR Entry ---
            double brEntryX = r0 + w - rBR;
            double brEntryY = z0;

            if (Math.Abs(brEntryX - startX) > 1e-6)
            {
                int pNext = Gmsh.Model.Occ.AddPoint(brEntryX, brEntryY, 0);
                loopCurves.Add(Gmsh.Model.Occ.AddLine(currentTag, pNext));
                currentTag = pNext;
            }

            // --- 2. BR Corner: BR Entry -> BR Exit ---
            if (rBR > 0)
            {
                double brExitX = r0 + w;
                double brExitY = z0 + rBR;
                int pExit = Gmsh.Model.Occ.AddPoint(brExitX, brExitY, 0);
                int center = Gmsh.Model.Occ.AddPoint(r0 + w - rBR, z0 + rBR, 0);
                loopCurves.Add(Gmsh.Model.Occ.AddCircleArc(currentTag, center, pExit));
                currentTag = pExit;
            }

            // --- 3. Right Edge: BR Exit -> TR Entry ---
            double trEntryX = r0 + w;
            double trEntryY = z0 + h - rTR;

            if (Math.Abs(trEntryY - (z0 + rBR)) > 1e-6)
            {
                int pNext = Gmsh.Model.Occ.AddPoint(trEntryX, trEntryY, 0);
                loopCurves.Add(Gmsh.Model.Occ.AddLine(currentTag, pNext));
                currentTag = pNext;
            }

            // --- 4. TR Corner: TR Entry -> TR Exit ---
            if (rTR > 0)
            {
                double trExitX = r0 + w - rTR;
                double trExitY = z0 + h;
                int pExit = Gmsh.Model.Occ.AddPoint(trExitX, trExitY, 0);
                int center = Gmsh.Model.Occ.AddPoint(r0 + w - rTR, z0 + h - rTR, 0);
                loopCurves.Add(Gmsh.Model.Occ.AddCircleArc(currentTag, center, pExit));
                currentTag = pExit;
            }

            // --- 5. Top Edge: TR Exit -> TL Entry ---
            double tlEntryX = r0 + rTL;
            double tlEntryY = z0 + h;

            if (Math.Abs(tlEntryX - (r0 + w - rTR)) > 1e-6)
            {
                int pNext = Gmsh.Model.Occ.AddPoint(tlEntryX, tlEntryY, 0);
                loopCurves.Add(Gmsh.Model.Occ.AddLine(currentTag, pNext));
                currentTag = pNext;
            }

            // --- 6. TL Corner: TL Entry -> TL Exit ---
            if (rTL > 0)
            {
                double tlExitX = r0;
                double tlExitY = z0 + h - rTL;
                int pExit = Gmsh.Model.Occ.AddPoint(tlExitX, tlExitY, 0);
                int center = Gmsh.Model.Occ.AddPoint(r0 + rTL, z0 + h - rTL, 0);
                loopCurves.Add(Gmsh.Model.Occ.AddCircleArc(currentTag, center, pExit));
                currentTag = pExit;
            }

            // --- 7. Left Edge: TL Exit -> BL Entry ---
            double blEntryX = r0;
            double blEntryY = z0 + rBL;

            if (Math.Abs(blEntryY - (z0 + h - rTL)) > 1e-6)
            {
                if (rBL == 0)
                {
                    loopCurves.Add(Gmsh.Model.Occ.AddLine(currentTag, startTag));
                    currentTag = startTag;
                }
                else
                {
                    int pNext = Gmsh.Model.Occ.AddPoint(blEntryX, blEntryY, 0);
                    loopCurves.Add(Gmsh.Model.Occ.AddLine(currentTag, pNext));
                    currentTag = pNext;
                }
            }

            // --- 8. BL Corner: BL Entry -> BL Exit (Start) ---
            if (rBL > 0)
            {
                int center = Gmsh.Model.Occ.AddPoint(r0 + rBL, z0 + rBL, 0);
                loopCurves.Add(Gmsh.Model.Occ.AddCircleArc(currentTag, center, startTag));
                currentTag = startTag;
            }

            // Create loop and surface
            int loop = Gmsh.Model.Occ.AddCurveLoop(loopCurves.ToArray());
            int s = Gmsh.Model.Occ.AddPlaneSurface(new[] { loop });

            return s;
        }

        public static (double x, double y) PointCom(int ptag)
        {
            Gmsh.Model.Occ.GetCenterOfMass(0, ptag, out double x, out double y, out double _);
            return (x, y);
        }

        public static (double x, double y) CurveCom(int ctag)
        {
            Gmsh.Model.Occ.GetCenterOfMass(1, ctag, out double x, out double y, out double _);
            return (x, y);
        }

        public static (double x, double y) SurfCom(int stag)
        {
            Gmsh.Model.Occ.GetCenterOfMass(2, stag, out double x, out double y, out double _);
            return (x, y);
        }

        public static bool InsideRect(double cx, double cy, double r0, double z0, double w, double h, double pad = 1.0)
        {
            return (r0 - pad <= cx && cx <= r0 + w + pad) && (z0 - pad <= cy && cy <= z0 + h + pad);
        }

        /// <summary>
        /// Create rectangle for the winding block and fillet only the top-left and top-right corners.
        /// Returns the surface tag (pre-boolean).
        /// </summary>
        public static int TopFilletBlock(WindingBlock b)
        {
            return AddRectWithCornerRadii(b.R0, b.ZBottom, b.Width, b.Height, b.FilletR, b.FilletR, 0.0, 0.0);
        }

        /// <summary>
        /// Build a pressboard barrier, optionally with tapered ends.
        /// </summary>
        public static int AddPressboardBarrier(PressboardBarrier pb)
        {
            double r0 = pb.R0;
            double z0 = pb.ZBottom;
            double t = pb.Thickness;
            double h = pb.Height;

            // If no tapers, just return a rectangle
            if (pb.TaperTop == null && pb.TaperBottom == null)
            {
                return AddRect(r0, z0, t, h);
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
            var ptTags = points.Select(p => Gmsh.Model.Occ.AddPoint(p.r, p.z, 0)).ToList();

            // Create lines connecting consecutive points
            var curves = new List<int>();
            int n = ptTags.Count;
            for (int i = 0; i < n; i++)
            {
                curves.Add(Gmsh.Model.Occ.AddLine(ptTags[i], ptTags[(i + 1) % n]));
            }

            int loop = Gmsh.Model.Occ.AddCurveLoop(curves.ToArray());
            int s = Gmsh.Model.Occ.AddPlaneSurface(new[] { loop });
            return s;
        }

        /// <summary>
        /// Build an L-shaped angle ring with rounded corners, respecting orientation signs.
        /// Optionally includes a taper at the tip of the vertical leg.
        /// </summary>
        public static int AddAngleRing(AngleRing ar)
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

            var curves = new List<int>();
            var tipCurves = new List<int>();
            int p1, p8;
            int? innerTaperCurve = null;
            int? outerTaperCurve = null;

            if (taper == null)
            {
                p1 = Gmsh.Model.Occ.AddPoint(r0 + Sr * Tv, zTip, 0);
                p8 = Gmsh.Model.Occ.AddPoint(r0, zTip, 0);
                tipCurves.Add(Gmsh.Model.Occ.AddLine(p1, p8));
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

                    int p1Tip = Gmsh.Model.Occ.AddPoint(rInnerTip, zTip, 0);
                    int p1Trans = Gmsh.Model.Occ.AddPoint(rInnerTrans, zTaperStart, 0);
                    p8 = Gmsh.Model.Occ.AddPoint(r0, zTip, 0);

                    p1 = p1Trans;
                    tipCurves.Add(Gmsh.Model.Occ.AddLine(p1Tip, p8));
                    innerTaperCurve = Gmsh.Model.Occ.AddLine(p1Trans, p1Tip);
                }
                else // "outer"
                {
                    double rOuterTip = r0 + Sr * (Tv - tEnd);
                    double rOuterTrans = r0;

                    p1 = Gmsh.Model.Occ.AddPoint(r0 + Sr * Tv, zTip, 0);
                    int p8Tip = Gmsh.Model.Occ.AddPoint(rOuterTip, zTip, 0);
                    int p8Trans = Gmsh.Model.Occ.AddPoint(rOuterTrans, zTaperStart, 0);

                    p8 = p8Trans;
                    tipCurves.Add(Gmsh.Model.Occ.AddLine(p1, p8Tip));
                    outerTaperCurve = Gmsh.Model.Occ.AddLine(p8Tip, p8Trans);
                }
            }

            // 2. Vertical Leg Tangent (Inner)
            int p2 = Gmsh.Model.Occ.AddPoint(r0 + Sr * Tv, Cy, 0);

            // Center point for fillet arcs
            int pc = Gmsh.Model.Occ.AddPoint(Cx, Cy, 0);

            // 3. Horizontal Leg Tangent (Inner)
            int p3 = Gmsh.Model.Occ.AddPoint(Cx, z0 + Sz * Th, 0);

            // 4. Horizontal Leg Tip (Inner)
            int p4 = Gmsh.Model.Occ.AddPoint(r0 + Sr * W, z0 + Sz * Th, 0);

            // 5. Horizontal Leg Tip (Outer)
            int p5 = Gmsh.Model.Occ.AddPoint(r0 + Sr * W, z0, 0);

            // 6. Horizontal Leg Tangent (Outer)
            int p6 = Gmsh.Model.Occ.AddPoint(Cx, z0, 0);

            // 7. Vertical Leg Tangent (Outer)
            int p7 = Gmsh.Model.Occ.AddPoint(r0, Cy, 0);

            // Build curves CCW around the L-shape
            // Tip of Vertical Leg
            curves.AddRange(tipCurves);

            if (taper != null && taper.Value.Side == "outer" && outerTaperCurve.HasValue)
            {
                curves.Add(outerTaperCurve.Value);
            }

            // Outer Vertical Edge (p8 to p7)
            curves.Add(Gmsh.Model.Occ.AddLine(p8, p7));

            // Outer Corner Arc (p7 to p6)
            curves.Add(Gmsh.Model.Occ.AddCircleArc(p7, pc, p6));

            // Outer Horizontal Edge (p6 to p5)
            curves.Add(Gmsh.Model.Occ.AddLine(p6, p5));

            // Tip of Horizontal Leg
            curves.Add(Gmsh.Model.Occ.AddLine(p5, p4));

            // Inner Horizontal Edge (p4 to p3)
            curves.Add(Gmsh.Model.Occ.AddLine(p4, p3));

            // Inner Corner Arc (p3 to p2)
            curves.Add(Gmsh.Model.Occ.AddCircleArc(p3, pc, p2));

            // Inner Vertical Edge (p2 to p1)
            if (taper != null && taper.Value.Side == "inner" && innerTaperCurve.HasValue)
            {
                curves.Add(Gmsh.Model.Occ.AddLine(p2, p1));
                curves.Add(innerTaperCurve.Value);
            }
            else
            {
                curves.Add(Gmsh.Model.Occ.AddLine(p2, p1));
            }

            int loop = Gmsh.Model.Occ.AddCurveLoop(curves.ToArray());
            int s = Gmsh.Model.Occ.AddPlaneSurface(new[] { loop });

            return s;
        }

        public static (double r, double z, double w, double h) AngleRingBbox(AngleRing ar)
        {
            double[] rPts = { ar.R0, ar.R0 + ar.Wh };
            double[] zPts = { ar.ZCorner, ar.ZCorner + ar.Hv };
            return (rPts.Min(), zPts.Min(), Math.Abs(ar.Wh), Math.Abs(ar.Hv));
        }

        /// <summary>
        /// Returns (metal_surface_tag, paper_surface_tags)
        /// </summary>
        public static (int metal, List<int> paper) AddStaticRing(StaticRing sr)
        {
            double z0M = sr.ZBottom;

            int metal = AddRectWithCornerRadii(
                r0: sr.R0, z0: z0M, w: sr.Width, h: sr.Height,
                rTL: sr.RTL, rTR: sr.RTR, rBR: sr.RBR, rBL: sr.RBL
            );
            Gmsh.Model.Occ.Synchronize();

            double t = sr.TPaper;
            double r0P = sr.R0 - t;
            double z0P = z0M - t;
            double wP = sr.Width + 2 * t;
            double hP = sr.Height + 2 * t;

            int paperOuter = AddRectWithCornerRadii(
                r0: r0P, z0: z0P, w: wP, h: hP,
                rTL: sr.RTL + t, rTR: sr.RTR + t, rBR: sr.RBR + t, rBL: sr.RBL + t
            );
            Gmsh.Model.Occ.Synchronize();

            Gmsh.Model.Occ.Cut(
                new[] { (2, paperOuter) },
                new[] { (2, metal) },
                out var outDimTags,
                out var _,
                removeObject: true,
                removeTool: false
            );
            Gmsh.Model.Occ.Synchronize();

            var paperTags = outDimTags.Where(dt => dt.dim == 2).Select(dt => dt.tag).ToList();
            if (paperTags.Count == 0)
            {
                throw new InvalidOperationException($"StaticRing {sr.Name}: cut produced no paper surfaces.");
            }
            return (metal, paperTags);
        }

        public static (double r, double z, double w, double h) StaticRingMetalBbox(StaticRing sr)
        {
            return (sr.R0, sr.ZBottom, sr.Width, sr.Height);
        }

        public static (double r, double z, double w, double h) StaticRingPaperBbox(StaticRing sr)
        {
            double t = sr.TPaper;
            return (sr.R0 - t, sr.ZBottom - t, sr.Width + 2 * t, sr.Height + 2 * t);
        }

        // ----------------------------
        // Physical group helpers
        // ----------------------------

        public static List<int> Uniq(IEnumerable<int> seq)
        {
            return seq.Distinct().OrderBy(x => x).ToList();
        }

        public static List<int> BoundaryCurvesOfSurfaces(IEnumerable<int> surfs)
        {
            var entities = surfs.Select(s => (2, s)).ToArray();
            Gmsh.Model.GetBoundary(entities, out var boundary, oriented: false, recursive: false);
            return Uniq(boundary.Where(b => b.dim == 1).Select(b => b.tag));
        }

        public static int AddPhys(int dim, List<int> tags, string name, int tag)
        {
            if (tags.Count == 0)
            {
                throw new InvalidOperationException($"Physical group '{name}' is empty.");
            }
            int pg = Gmsh.Model.AddPhysicalGroup(dim, tags.ToArray(), tag);
            Gmsh.Model.SetPhysicalName(dim, pg, name);
            return pg;
        }

        public static List<int> MappedChildrenSurfaces(IEnumerable<int> seedSurfs, double eps = 1e-3)
        {
            var kids = new List<int>();
            foreach (int s in seedSurfs)
            {
                var (cx, cy) = SurfCom(s);
                Gmsh.Model.GetEntitiesInBoundingBox(
                    cx - eps, cy - eps, -1e-6,
                    cx + eps, cy + eps, 1e-6,
                    out var hits, 2);
                kids.AddRange(hits.Where(h => h.dim == 2).Select(h => h.tag));
            }
            return Uniq(kids);
        }

        // ----------------------------
        // GetDP Analysis
        // ----------------------------

        public static int RunGetDPAnalysis(
            string mshPath,
            string resPath,
            string proFile = "electrostatics_axisym.pro",
            bool verbose = true)
        {
            string resDir = Path.GetDirectoryName(resPath);
            if (string.IsNullOrEmpty(resDir)) resDir = ".";
            Directory.CreateDirectory(resDir);

            var args = new[]
            {
                proFile,
                "-setstring", "modelPath", $"{resDir}/",
                "-msh", mshPath,
                "-solve", "Electrostatics_v",
                "-pos", "Map"
            };

            if (verbose)
            {
                Console.WriteLine($"Running GetDP: getdp {string.Join(" ", args)}");
            }

            var psi = new ProcessStartInfo("getdp", string.Join(" ", args))
            {
                RedirectStandardOutput = !verbose,
                RedirectStandardError = !verbose,
                UseShellExecute = false
            };

            using var process = Process.Start(psi);
            process?.WaitForExit();

            int exitCode = process?.ExitCode ?? -1;

            if (exitCode == 0 && verbose)
            {
                Console.WriteLine($"GetDP analysis completed successfully. Results in: {resPath}");
            }
            else if (exitCode != 0)
            {
                Console.WriteLine($"GetDP analysis failed with exit code {exitCode}");
            }

            return exitCode;
        }

        // ----------------------------
        // Build
        // ----------------------------

        public static void BuildModel(
            Domain domain,
            List<WindingBlock> windings,
            List<PressboardBarrier> pressboards,
            List<AngleRing> angleRings,
            List<StaticRing> staticRings,
            double lc = 1.0,
            string mshOut = "msh/geom.msh",
            bool verbose = true)
        {
            Gmsh.Initialize();
            Gmsh.Option.SetNumber("General.Terminal", verbose ? 1 : 0);
            Gmsh.Model.Add("axisym_param");

            // Outer computational region (oil box)
            int oil0 = AddRect(domain.RInner, domain.ZLower,
                               domain.ROuter - domain.RInner,
                               domain.ZUpper - domain.ZLower);

            // --- Create components and track their tags
            var windingTags = new Dictionary<string, int>();
            foreach (var w in windings)
            {
                windingTags[w.Name] = TopFilletBlock(w);
            }

            var pbTags = new Dictionary<string, int>();
            foreach (var pb in pressboards)
            {
                pbTags[pb.Name] = AddPressboardBarrier(pb);
            }

            var arTags = new Dictionary<string, int>();
            foreach (var ar in angleRings)
            {
                arTags[ar.Name] = AddAngleRing(ar);
            }

            var staticMetalSeed = new Dictionary<string, int>();
            var staticPaperSeed = new Dictionary<string, List<int>>();

            foreach (var sr in staticRings)
            {
                var (metalTag, paperTags) = AddStaticRing(sr);
                staticMetalSeed[sr.Name] = metalTag;
                staticPaperSeed[sr.Name] = paperTags;
            }

            Gmsh.Model.Occ.Synchronize();

            // --- Prepare for Fragment
            var inputMap = new Dictionary<int, (string category, string name)>();

            foreach (var (name, tag) in windingTags)
            {
                inputMap[tag] = ("winding", name);
            }

            foreach (var (name, tag) in pbTags)
            {
                inputMap[tag] = ("pressboard", name);
            }

            foreach (var (name, tag) in arTags)
            {
                inputMap[tag] = ("angle_ring", name);
            }

            foreach (var sr in staticRings)
            {
                int metalTag = staticMetalSeed[sr.Name];
                inputMap[metalTag] = ("static_metal", sr.Name);
                foreach (int t in staticPaperSeed[sr.Name])
                {
                    inputMap[t] = ("static_paper", sr.Name);
                }
            }

            // Build tools list
            var tools = inputMap.Keys.Select(tag => (2, tag)).ToArray();

            // --- Fragment
            Gmsh.Model.Occ.Fragment(
                new[] { (2, oil0) },
                tools,
                out var outDimTags,
                out var outDimTagsMap,
                removeObject: true,
                removeTool: true
            );
            Gmsh.Model.Occ.Synchronize();

            // --- Process Fragment Results
            var domainFragments = new HashSet<int>(
                outDimTagsMap[0].Where(dt => dt.dim == 2).Select(dt => dt.tag)
            );

            var electrodeSurfs = windings.ToDictionary(w => w.Name, _ => new List<int>());
            var pressboardSurfs = pressboards.ToDictionary(pb => pb.Name, _ => new List<int>());
            var angleRingSurfs = angleRings.ToDictionary(ar => ar.Name, _ => new List<int>());
            var staticMetalSurfs = staticRings.ToDictionary(sr => sr.Name, _ => new List<int>());
            var staticPaperSurfs = staticRings.ToDictionary(sr => sr.Name, _ => new List<int>());

            var allComponentSurfs = new HashSet<int>();
            var surfacesToRemove = new List<int>();

            for (int i = 0; i < tools.Length; i++)
            {
                int inputTag = tools[i].Item2;
                var fragments = outDimTagsMap[i + 1].Where(dt => dt.dim == 2).Select(dt => dt.tag).ToList();

                var inside = fragments.Where(t => domainFragments.Contains(t)).ToList();
                var outside = fragments.Where(t => !domainFragments.Contains(t)).ToList();

                if (outside.Count > 0)
                {
                    surfacesToRemove.AddRange(outside);
                }

                if (inside.Count == 0) continue;

                var (category, name) = inputMap[inputTag];

                switch (category)
                {
                    case "winding":
                        electrodeSurfs[name].AddRange(inside);
                        break;
                    case "pressboard":
                        pressboardSurfs[name].AddRange(inside);
                        break;
                    case "angle_ring":
                        angleRingSurfs[name].AddRange(inside);
                        break;
                    case "static_metal":
                        staticMetalSurfs[name].AddRange(inside);
                        break;
                    case "static_paper":
                        staticPaperSurfs[name].AddRange(inside);
                        break;
                }

                allComponentSurfs.UnionWith(inside);
            }

            // Remove outside surfaces
            if (surfacesToRemove.Count > 0)
            {
                Gmsh.Model.Occ.Remove(surfacesToRemove.Select(t => (2, t)).ToArray(), recursive: true);
                Gmsh.Model.Occ.Synchronize();
            }

            // --- Identify Oil Surfaces
            var oilSurfs = domainFragments.Where(t => !allComponentSurfs.Contains(t)).ToList();

            // --- Collect all surfaces
            Gmsh.Model.Occ.GetEntities(out var allEntities, 2);
            var allSurfs = allEntities.Select(e => e.tag).ToList();

            // Unions
            var electrodeUnion = new HashSet<int>(electrodeSurfs.Values.SelectMany(x => x));
            var pressboardUnion = new HashSet<int>(pressboardSurfs.Values.SelectMany(x => x));
            var angleRingUnion = new HashSet<int>(angleRingSurfs.Values.SelectMany(x => x));
            var staticMetalUnion = new HashSet<int>(staticMetalSurfs.Values.SelectMany(x => x));
            var staticPaperUnion = new HashSet<int>(staticPaperSurfs.Values.SelectMany(x => x));

            // ---- 2D materials ----
            AddPhys(2, oilSurfs, "OIL", tag: 1);

            var pbTotal = pressboardUnion.Union(angleRingUnion).ToList();
            if (pbTotal.Count > 0)
            {
                AddPhys(2, pbTotal.OrderBy(x => x).ToList(), "PRESSBOARD", tag: 2);
            }

            if (staticPaperUnion.Count > 0)
            {
                AddPhys(2, staticPaperUnion.OrderBy(x => x).ToList(), "PAPER", tag: 3);
            }

            // ---- electrodes (surfaces only for bookkeeping) ----
            var electrodeTagMap = new Dictionary<string, int>
            {
                { "HV", 101 }, { "LV", 102 }, { "RV", 103 },
                { "HV_2", 104 }, { "LV_2", 105 }, { "RV_2", 106 }
            };

            foreach (var w in windings)
            {
                AddPhys(2, electrodeSurfs[w.Name], $"ELECTRODE_{w.Name}", tag: electrodeTagMap[w.Name]);
            }

            for (int i = 0; i < staticRings.Count; i++)
            {
                var sr = staticRings[i];
                AddPhys(2, staticMetalSurfs[sr.Name], $"ELECTRODE_{sr.Name}", tag: 120 + i);
            }

            // Curves we want to refine around
            var refineCurves = new List<int>();

            // --- Physical groups (1D boundaries for Dirichlet BC)
            var bcIds = new Dictionary<string, int>
            {
                { "HV", 11 }, { "LV", 12 }, { "RV", 13 },
                { "HV_2", 14 }, { "LV_2", 15 }, { "RV_2", 16 }
            };

            foreach (var w in windings)
            {
                var curves = BoundaryCurvesOfSurfaces(electrodeSurfs[w.Name]);
                AddPhys(1, curves, $"BC_{w.Name}", tag: bcIds[w.Name]);
                refineCurves.AddRange(curves);
            }

            for (int i = 0; i < staticRings.Count; i++)
            {
                var sr = staticRings[i];
                var curves = BoundaryCurvesOfSurfaces(staticMetalSurfs[sr.Name]);
                AddPhys(1, curves, $"BC_{sr.Name}", tag: 21 + i);
                refineCurves.AddRange(curves);
            }

            // Optional: pressboard boundary curves
            if (pbTotal.Count > 0)
            {
                var pbCurves = BoundaryCurvesOfSurfaces(pbTotal.OrderBy(x => x));
                AddPhys(1, pbCurves, "BC_PRESSBOARD", tag: 30);
                refineCurves.AddRange(pbCurves);
            }

            refineCurves = Uniq(refineCurves);

            // Outer boundary ("tank")
            Gmsh.Model.Occ.GetEntities(out var allCurveEntities, 1);
            var allCurves = allCurveEntities.Select(e => e.tag).ToList();
            var tankCurves = new List<int>();
            var bottomCurves = new List<int>();

            foreach (int c in allCurves)
            {
                var (cx, cy) = CurveCom(c);

                if (Math.Abs(cy - domain.ZLower) < 1e-3)
                {
                    bottomCurves.Add(c);
                }
                else if (Math.Abs(cx - domain.RInner) < 1e-3 ||
                         Math.Abs(cx - domain.ROuter) < 1e-3 ||
                         Math.Abs(cy - domain.ZUpper) < 1e-3)
                {
                    tankCurves.Add(c);
                }
            }

            AddPhys(1, Uniq(tankCurves), "BC_TANK", tag: 17);
            AddPhys(1, Uniq(bottomCurves), "BC_BOTTOM", tag: 18);

            // --- Local refinement near selected curves
            if (refineCurves.Count > 0)
            {
                int fDist = Gmsh.Model.Mesh.Field.Add("Distance");
                Gmsh.Model.Mesh.Field.SetNumbers(fDist, "CurvesList", refineCurves.Select(c => (double)c).ToArray());
                Gmsh.Model.Mesh.Field.SetNumber(fDist, "Sampling", 100);

                int fTh = Gmsh.Model.Mesh.Field.Add("Threshold");
                Gmsh.Model.Mesh.Field.SetNumber(fTh, "InField", fDist);
                Gmsh.Model.Mesh.Field.SetNumber(fTh, "SizeMin", Math.Max(0.2, 0.15 * lc));
                Gmsh.Model.Mesh.Field.SetNumber(fTh, "SizeMax", lc);
                Gmsh.Model.Mesh.Field.SetNumber(fTh, "DistMin", 0.5 * lc);
                Gmsh.Model.Mesh.Field.SetNumber(fTh, "DistMax", 6.0 * lc);

                Gmsh.Model.Mesh.Field.SetAsBackgroundMesh(fTh);
            }

            // --- Mesh
            Gmsh.Option.SetNumber("Mesh.CharacteristicLengthMax", lc);
            Gmsh.Option.SetNumber("Mesh.CharacteristicLengthMin", 0.1);
            Gmsh.Option.SetNumber("Mesh.SaveAll", 1);
            Gmsh.Option.SetNumber("Mesh.ElementOrder", 2);
            Gmsh.Option.SetNumber("Mesh.MeshSizeFromPoints", 0);
            Gmsh.Option.SetNumber("Mesh.MeshSizeFromCurvature", 0);
            Gmsh.Option.SetNumber("Mesh.MeshSizeExtendFromBoundary", 0);

            Gmsh.Model.Mesh.Generate(2);

            // Ensure output directory exists
            string meshDir = Path.GetDirectoryName(mshOut);
            if (string.IsNullOrEmpty(meshDir)) meshDir = ".";
            Directory.CreateDirectory(meshDir);

            Gmsh.Write(mshOut);

            // Export geometry to BREP format
            string brepOut = mshOut.Replace(".msh", ".brep");
            if (brepOut == mshOut) brepOut += ".brep";
            Gmsh.Write(brepOut);

            if (verbose)
            {
                Console.WriteLine($"Wrote: {mshOut}");
                Console.WriteLine($"Wrote: {brepOut}");
                Console.WriteLine($"  oil surfaces: {oilSurfs.Count}");
                Console.WriteLine($"  pressboard surfaces: {pbTotal.Count}");
                Console.WriteLine($"  paper surfaces: {staticPaperUnion.Count}");
                foreach (var w in windings)
                {
                    Console.WriteLine($"  {w.Name} electrode surfaces: {electrodeSurfs[w.Name].Count}");
                }
                foreach (var sr in staticRings)
                {
                    Console.WriteLine($"  {sr.Name} electrode surfaces: {staticMetalSurfs[sr.Name].Count}");
                }
            }

            Gmsh.Finalize();
        }
    }

}
