using GeometryLib;
using System;
using System.Collections.Generic;
using System.Linq;
using TfmrLib;
using TfmrLib.FEM;

namespace electrostat
{
    public static class GeometryBuilder
    {
        /// <summary>
        /// Create rectangle for the winding block and fillet only the top-left and top-right corners.
        /// Returns the surface tag (pre-boolean).
        /// </summary>
        public static GeomSurface TopFilletBlock(Geometry geometry, WindingBlock b)
        {
            return geometry.AddRectWithCornerRadii(b.R0, b.ZBottom, b.Width, b.Height, b.FilletR, b.FilletR, 0.0, 0.0);
        }

        /// <summary>
        /// Build a pressboard barrier, optionally with tapered ends.
        /// </summary>
        public static GeomSurface AddPressboardBarrier(Geometry geometry, PressboardBarrier pb)
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
        public static GeomSurface AddAngleRing(Geometry geometry, AngleRing ar)
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
        public static (GeomSurface metal, GeomSurface paper) AddStaticRing(Geometry geometry, StaticRing sr)
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

        // ----------------------------
        // Build
        // ----------------------------

        /// <summary>
        /// Build the geometry and mesh for <paramref name="caseName"/> and create the
        /// <see cref="FEMProblem"/> (materials, regions, Dirichlet boundary conditions, and
        /// the voltage <see cref="Scenario"/>s) WITHOUT solving. Call <see cref="BuiltModel.Solve"/>
        /// to run every scenario against the same mesh in a single solver invocation,
        /// avoiding a gmsh re-mesh per scenario.
        /// </summary>
        public static BuiltModel BuildMesh(
            ElectrostatCase _case,
            string caseName,
            double lc = 5.0,
            bool clipToDomain = true,
            AmrSettings? amr = null)
        {
            return BuildModel(
                _case,
                lc: lc,
                mshOut: $"{caseName}/geom.msh",
                clipToDomain: clipToDomain,
                generateMesh: true,
                amr: amr);
        }

        /// <summary>
        /// Build the geometry without invoking gmsh / writing a mesh file.
        /// Returns the populated <see cref="BuiltModel"/> for visualization.
        /// </summary>
        public static BuiltModel BuildGeometryOnly(
            ElectrostatCase _case,
            bool clipToDomain = true)
        {
            return BuildModel(_case, lc: 5.0, mshOut: null, clipToDomain: clipToDomain, generateMesh: false);
        }

        /// <summary>
        /// Discriminates the kinds of geometry component the builder produces. Replaces the
        /// former magic-string "type" discriminator ("winding", "pressboard", ...).
        /// </summary>
        private enum ComponentKind
        {
            Winding,
            Pressboard,
            AngleRing,
            StaticMetal,
            StaticPaper,
        }

        /// <summary>
        /// A single built geometry component: its surface, case-defined name, and kind.
        /// <see cref="Surf"/> is a reference type, so phases that mutate <c>Surf.Boundary</c>
        /// (e.g. clipping) are observed through every derived view that yields this record.
        /// </summary>
        private readonly record struct BuiltComponent(GeomSurface Surf, string Name, ComponentKind Kind);

        // Mutable working state shared across the BuildModel phase helpers. Replaces the
        // large pile of locals the monolithic BuildModel used to thread through its body;
        // each phase reads what it needs and writes back what later phases consume.
        private sealed class BuildContext
        {
            public required ElectrostatCase Case;
            public required MFEMProblem Problem;
            public required Geometry Geom;
            public TagManager Tags = new();
            public Material Oil = null!;
            public Material Paper = null!;
            public Material Pressboard = null!;
            public GeomSurface OilSurf = null!;

            // Per-build lookup caches. These used to be mutable statics on GeometryBuilder;
            // they now live on the context so each build owns its own state and the finished
            // BuiltModel can take them over.
            public readonly Dictionary<string, List<GeomSurface>> ComponentSurfaces = new();
            public readonly List<GeomSurface> ElectrodeSurfaces = new();
            public readonly List<GeomLineLoop> DirichletWallLoops = new();

            public void RegisterComponentSurface(string name, GeomSurface surf)
            {
                if (string.IsNullOrEmpty(name) || surf == null) return;
                if (!ComponentSurfaces.TryGetValue(name, out var list))
                    ComponentSurfaces[name] = list = new List<GeomSurface>();
                list.Add(surf);
            }

            // Single source of truth for every component produced in BuildComponents, in
            // creation order. The two views below replace the former parallel lists; both
            // preserve the original iteration order exactly.
            public readonly List<BuiltComponent> Components = new();

            // Conductors that participate only as Dirichlet boundaries (windings + static-ring
            // metals). Static-ring metal is deliberately excluded from ComponentsToClip because
            // it sits inside the paper, which is the surface that actually touches the oil.
            public IEnumerable<BuiltComponent> Electrodes =>
                Components.Where(c => c.Kind is ComponentKind.Winding or ComponentKind.StaticMetal);

            // Everything that gets clipped to the domain and punched into the oil region as a
            // hole: windings, pressboards, angle rings, and static-ring paper.
            public IEnumerable<BuiltComponent> ComponentsToClip =>
                Components.Where(c => c.Kind != ComponentKind.StaticMetal);

            public readonly List<GeomLineLoop> RefineLoops = new();
            public readonly List<(GeomLineLoop loop, int tag)> ElectrodeLoopTags = new();
            public int ClippedCount;
            public int SkippedCount;
        }

        public static BuiltModel BuildModel(
            ElectrostatCase _case,
            double lc = 100.0,
            string? mshOut = "msh/geom.msh",
            bool clipToDomain = true,
            bool generateMesh = true,
            AmrSettings? amr = null)
        {
            MeshGenerator gmsh = new MeshGenerator();

            var geometry = new Geometry();
            var mfemProblem = new MFEMProblem();

            // Carry the case's coordinate system through to the solver. The mesh / geometry
            // is identical across geometry types (only this flag varies per cut), so the
            // same build feeds an axisymmetric (r–z) or planar solve unchanged.
            mfemProblem.GeometryType = _case.GeometryType;

            // Optional adaptive mesh refinement. Null leaves the solver in its previous
            // single-solve mode; when supplied (and enabled) the solver runs its AMR loop
            // and returns the final refined mesh through the usual results contract.
            mfemProblem.Amr = amr;

            var ctx = new BuildContext
            {
                Case = _case,
                Problem = mfemProblem,
                Geom = geometry,
            };

            AddMaterials(ctx);
            BuildOuterDomain(ctx);
            BuildComponents(ctx);
            ClipComponentsToDomain(ctx, clipToDomain);
            DropElectrodeInteriors(ctx);
            ConditionGeometryAndTagElectrodes(ctx);

            if (clipToDomain)
            {
                Console.WriteLine($"Domain clipping summary: {ctx.ClippedCount} components clipped, {ctx.SkippedCount} components removed");
            }

            AddElectrodeBoundaryConditions(ctx);

            AddScenarios(ctx);

            GenerateMeshPhase(ctx, gmsh, mshOut, lc, generateMesh);

            return new BuiltModel
            {
                Geometry = geometry,
                Problem = mfemProblem,
                ComponentSurfaces = ctx.ComponentSurfaces,
                ElectrodeSurfaces = ctx.ElectrodeSurfaces,
                DirichletWallLoops = ctx.DirichletWallLoops,
                SurfaceCategories = BuildSurfaceCategoryMap(ctx),
            };
        }

        /// <summary>
        /// Map each built surface to a semantic <see cref="SurfaceCategory"/> so the UI can
        /// color regions by what they represent. Includes the oil domain surface and every
        /// component surface (keyed by reference identity, mirroring how the geometry view
        /// receives the same surface instances).
        /// </summary>
        private static IReadOnlyDictionary<GeomSurface, SurfaceCategory> BuildSurfaceCategoryMap(BuildContext ctx)
        {
            var map = new Dictionary<GeomSurface, SurfaceCategory>(ReferenceEqualityComparer.Instance);

            if (ctx.OilSurf != null)
                map[ctx.OilSurf] = SurfaceCategory.Oil;

            foreach (var (surf, _, kind) in ctx.Components)
            {
                if (surf == null) continue;
                map[surf] = kind switch
                {
                    ComponentKind.Winding => SurfaceCategory.Winding,
                    ComponentKind.Pressboard => SurfaceCategory.Pressboard,
                    ComponentKind.AngleRing => SurfaceCategory.AngleRing,
                    ComponentKind.StaticMetal => SurfaceCategory.StaticRingMetal,
                    ComponentKind.StaticPaper => SurfaceCategory.StaticRingPaper,
                    _ => SurfaceCategory.Other,
                };
            }

            return map;
        }

        /// <summary>Create the three dielectric materials (oil, paper, pressboard) and register them on the problem.</summary>
        private static void AddMaterials(BuildContext ctx)
        {
            var problem = ctx.Problem;

            var oil = new Material("Oil");
            oil.Properties.Add("epsilon_r", 2.2);
            problem.Materials.Add(oil);
            var paper = new Material("Paper");
            paper.Properties.Add("epsilon_r", 4.4);
            problem.Materials.Add(paper);
            var pressboard = new Material("Pressboard");
            pressboard.Properties.Add("epsilon_r", 4.4);
            problem.Materials.Add(pressboard);

            ctx.Oil = oil;
            ctx.Paper = paper;
            ctx.Pressboard = pressboard;
        }

        /// <summary>Build the outer oil box, its Oil region, the four domain-wall Dirichlet BCs, and the streamline wall loops.</summary>
        private static void BuildOuterDomain(BuildContext ctx)
        {
            var geometry = ctx.Geom;
            var problem = ctx.Problem;
            var tags = ctx.Tags;
            var domain = ctx.Case.Domain;
            var voltages = ctx.Case.Voltages;
            var oil = ctx.Oil;

            // Outer computational region (oil box)
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
            var oil_group = new EntityGroup { Name = "Oil", Dimension = 2, AttributeIds = new List<int> { oil_tag } };
            problem.EntityGroups.Add(oil_group);
            var region = new Region("Oil", oil_group.Name, oil);
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
            void AddDomainBC(string name, int tag)
            {
                if (tag <= 0) return;
                // Only pin a domain wall whose name appears in the case's voltages map.
                // Walls omitted from the map (e.g. "Lower_Bdry") are intentionally natural
                // (Neumann) boundaries; pinning them to 0 V over-constrains any winding
                // corner that gets clipped onto that wall (the shared corner DOF is then
                // driven by both the conductor terminal and this wall), producing MFEM's
                // "Conflicting Boundary Constraints" error. This mirrors the
                // RegisterWallElectrode guard below.
                if (!voltages.ContainsKey(name)) return;
                var wallGroup = new EntityGroup { Name = name, Dimension = 1, AttributeIds = new List<int> { tag } };
                problem.EntityGroups.Add(wallGroup);

                problem.BoundaryConditions.Add(new DirichletBoundaryCondition
                {
                    Name = name,
                    EntityGroupName = wallGroup.Name,
                    Potential = 0.0,
                });
            }

            AddDomainBC("Core", leftTag);
            AddDomainBC("TopYoke", topTag);
            AddDomainBC("Tank", rightTag);
            AddDomainBC("Lower_Bdry", bottomTag);

            // Mirror the same Dirichlet-wall information as single-edge loops so the
            // streamline clipper can terminate traces on these walls as if they were
            // electrode surfaces.
            void RegisterWallElectrode(string name, GeomLine edge)
            {
                if (!voltages.ContainsKey(name)) return;
                ctx.DirichletWallLoops.Add(new GeomLineLoop(new List<GeomEntity> { edge }));
            }
            RegisterWallElectrode("Core", left_bdry);
            RegisterWallElectrode("TopYoke", top_bdry);
            RegisterWallElectrode("Tank", right_bdry);
            RegisterWallElectrode("Lower_Bdry", bottom_bdry);

            ctx.OilSurf = oil_surf;
        }

        /// <summary>Create the full (unclipped) winding, pressboard, angle-ring, and static-ring surfaces and their regions/tags.</summary>
        private static void BuildComponents(BuildContext ctx)
        {
            var geometry = ctx.Geom;
            var problem = ctx.Problem;
            var tags = ctx.Tags;
            var paper = ctx.Paper;
            var pressboard = ctx.Pressboard;
            var components = ctx.Components;

            var windings = ctx.Case.Windings;
            var pressboards = ctx.Case.Pressboards;
            var angleRings = ctx.Case.AngleRings;
            var staticRings = ctx.Case.StaticRings;

            // --- Phase 1: Build FULL geometry (unrestricted by domain) ---
            foreach (var w in windings)
            {
                var wdg_block_surf = TopFilletBlock(geometry, w);
                //tags.TagEntityByString(wdg_block_surf, w.Name);
                components.Add(new BuiltComponent(wdg_block_surf, w.Name, ComponentKind.Winding));
                tags.TagEntityByString(wdg_block_surf.Boundary, w.Name + "Bdry");
                ctx.ElectrodeSurfaces.Add(wdg_block_surf);
                ctx.RegisterComponentSurface(w.Name, wdg_block_surf);
            }

            foreach (var pb in pressboards)
            {
                var pb_surf = AddPressboardBarrier(geometry, pb);
                int pb_tag = tags.TagEntityByString(pb_surf, pb.Name);
                components.Add(new BuiltComponent(pb_surf, pb.Name, ComponentKind.Pressboard));
                var pb_group = new EntityGroup { Name = pb.Name, Dimension = 2, AttributeIds = new List<int> { pb_tag } };
                problem.EntityGroups.Add(pb_group);
                var pb_region = new Region(pb.Name, pb_group.Name, pressboard);
                problem.Regions.Add(pb_region);
                ctx.RegisterComponentSurface(pb.Name, pb_surf);
            }

            foreach (var ar in angleRings)
            {
                var ar_surf = AddAngleRing(geometry, ar);
                int ar_tag = tags.TagEntityByString(ar_surf, ar.Name);
                components.Add(new BuiltComponent(ar_surf, ar.Name, ComponentKind.AngleRing));
                var ar_group = new EntityGroup { Name = ar.Name, Dimension = 2, AttributeIds = new List<int> { ar_tag } };
                problem.EntityGroups.Add(ar_group);
                var ar_region = new Region(ar.Name, ar_group.Name, pressboard);
                problem.Regions.Add(ar_region);
                ctx.RegisterComponentSurface(ar.Name, ar_surf);
            }

            foreach (var sr in staticRings)
            {
                var (metal_surf, paper_surf) = AddStaticRing(geometry, sr);
                // Add metal BEFORE paper so the Electrodes view (windings + metals) and the
                // ComponentsToClip view (everything except metal) each preserve the exact
                // iteration order the former parallel lists produced.
                //tags.TagEntityByString(metal_surf, sr.Name + "_Metal");
                tags.TagEntityByString(metal_surf.Boundary, sr.Name + "_Metal_Bdry");
                components.Add(new BuiltComponent(metal_surf, sr.Name + "_Metal", ComponentKind.StaticMetal));
                ctx.ElectrodeSurfaces.Add(metal_surf);
                int paper_tag = tags.TagEntityByString(paper_surf, sr.Name + "_Paper");
                components.Add(new BuiltComponent(paper_surf, sr.Name + "_Paper", ComponentKind.StaticPaper));
                var paper_group = new EntityGroup { Name = sr.Name + "_Paper", Dimension = 2, AttributeIds = new List<int> { paper_tag } };
                problem.EntityGroups.Add(paper_group);
                var paper_region = new Region(sr.Name+"_Paper", paper_group.Name, paper);
                problem.Regions.Add(paper_region);
                ctx.RegisterComponentSurface(sr.Name, paper_surf);
                ctx.RegisterComponentSurface(sr.Name, metal_surf);
            }
        }

        /// <summary>Clip each component to the domain, drop out-of-bounds/degenerate ones, and add survivors as oil holes / refine loops.</summary>
        private static void ClipComponentsToDomain(BuildContext ctx, bool clipToDomain)
        {
            var geometry = ctx.Geom;
            var domain = ctx.Case.Domain;
            var oil_surf = ctx.OilSurf;
            var componentSurfaces = ctx.ComponentsToClip;
            var refineLoops = ctx.RefineLoops;

            // --- Phase 2: Clip components to domain (if enabled) ---
            int clippedCount = 0;
            int skippedCount = 0;

            // Track surfaces that should be removed from the geometry (fully outside / degenerate).
            var surfacesToRemove = new List<GeomSurface>();

            foreach (var (surf, name, kind) in componentSurfaces)
            {
                var bounds = surf.Boundary.GetBoundingBox();

                // Guard against degenerate/invalid geometry
                if (double.IsNaN(bounds.minX) || double.IsNaN(bounds.maxX) ||
                    double.IsNaN(bounds.minY) || double.IsNaN(bounds.MaxY) ||
                    double.IsInfinity(bounds.minX) || double.IsInfinity(bounds.maxX) ||
                    double.IsInfinity(bounds.minY) || double.IsInfinity(bounds.MaxY))
                {
                    Console.WriteLine($"Warning: {name} ({kind}) has invalid bounds (NaN/Infinity), skipping");
                    surfacesToRemove.Add(surf);
                    skippedCount++;
                    continue;
                }

                // Check if component intersects domain - note: MaxY with capital M
                if (!domain.Intersects(bounds.minX, bounds.minY, bounds.maxX, bounds.MaxY))
                {
                    Console.WriteLine($"Info: {name} ({kind}) is entirely outside domain, removing");
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
                        Console.WriteLine($"Info: Clipping {name} ({kind}) to domain bounds");
                        var clippedBoundary = GeometryClipping.ClipLineLoop(surf.Boundary, domain, geometry);

                        if (clippedBoundary == null)
                        {
                            Console.WriteLine($"Warning: {name} ({kind}) became degenerate after clipping, removing");
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
                if (kind != ComponentKind.StaticMetal)
                {
                    oil_surf.Holes.Add(boundaryToAdd);
                }
                refineLoops.Add(boundaryToAdd);

                // Add material regions for non-electrodes
                if (kind is ComponentKind.Pressboard or ComponentKind.AngleRing)
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

            ctx.ClippedCount = clippedCount;
            ctx.SkippedCount = skippedCount;
        }

        /// <summary>Remove electrode interior surfaces (windings, static-ring metals) so gmsh does not emit orphan-node plane surfaces.</summary>
        private static void DropElectrodeInteriors(BuildContext ctx)
        {
            var geometry = ctx.Geom;
            var electrodes = ctx.Electrodes;

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
            // (ctx.Electrodes already contains only windings + static-ring metals.)
            var electrodeSurfacesToDrop = new HashSet<GeomSurface>(ReferenceEqualityComparer.Instance);
            foreach (var (surf, _, _) in electrodes)
            {
                electrodeSurfacesToDrop.Add(surf);
            }
            geometry.Surfaces.RemoveAll(s => electrodeSurfacesToDrop.Contains(s));
        }

        /// <summary>Capture electrode Dirichlet tags, run the topology conditioner (split/drop/prune), then re-tag surviving conductor sub-edges.</summary>
        private static void ConditionGeometryAndTagElectrodes(BuildContext ctx)
        {
            var geometry = ctx.Geom;
            var domain = ctx.Case.Domain;
            var electrodes = ctx.Electrodes;

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
            GeometryConditioner.SplitLinesAtIncidentPoints(geometry);

            // After splitting, an electrode hole loop may now share one or more edge instances
            // with the oil (or paper) outer boundary — e.g. a winding clipped to the bottom of
            // the domain shares its bottom edge with bottom_bdry. Such a "hole" is no longer
            // interior to the enclosing surface and would cause gmsh to insert orphan nodes
            // along the shared edge. Drop those hole loops here so the remaining surface has
            // a topologically consistent boundary.
            GeometryConditioner.DropMergedHoleLoops(geometry);

            // Prune any line loops / lines / arcs that are no longer referenced by any surface.
            // This is what was leaking outside-domain geometry.
            GeometryConditioner.PruneUnreferencedEntities(geometry);

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
                    if (GeometryConditioner.IsOnDomainEdge(ent, domain)) continue;
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

            ctx.ElectrodeLoopTags.AddRange(electrodeLoopTags);
        }

        /// <summary>Create an EntityGroup and a voltage Terminal for every electrode that has a resolved voltage.</summary>
        private static void AddElectrodeBoundaryConditions(BuildContext ctx)
        {
            var problem = ctx.Problem;
            var voltages = ctx.Case.Voltages;
            var electrodes = ctx.Electrodes;
            var electrodeLoopTags = ctx.ElectrodeLoopTags;

            foreach (var (surf, name, _) in electrodes)
            {
                // An electrode may lack an explicit entry (e.g. a newly added winding, or a
                // nested static ring whose parent winding was renamed). Skip it rather than
                // throwing KeyNotFoundException; the resolved voltage map normally supplies
                // every electrode via ElectrostatCase.EffectiveVoltages.
                if (!voltages.ContainsKey(name))
                {
                    Console.WriteLine($"Warning: no voltage specified for electrode '{name}', skipping its Dirichlet BC");
                    continue;
                }

                // Add boundary conditions for electrodes.
                // Use the tag we captured before clearing surf.Boundary.Tag above; that is
                // also the per-segment tag now applied to the conductor's surviving sub-edges,
                // which GmshFile aggregates into a single Physical Curve.
                int bcTag = electrodeLoopTags
                    .Where(t => ReferenceEquals(t.loop, surf.Boundary))
                    .Select(t => t.tag)
                    .DefaultIfEmpty(surf.Boundary.Tag)
                    .First();
                if (bcTag <= 0) continue;
                var group = new EntityGroup
                {
                    Name = name,
                    Dimension = 1,
                    AttributeIds = new List<int> { bcTag }
                };
                problem.EntityGroups.Add(group);

                // Create a terminal for this electrode (for scenario-based voltage control)
                var terminal = new TfmrLib.FEM.Terminal
                {
                    Name = name,
                    EntityGroup = group,
                    ExcitationType = Quantity.Voltage
                };
                problem.Terminals.Add(terminal);
            }
        }

        /// <summary>
        /// Translate the case's voltage permutations into <see cref="FEMProblem.Scenarios"/>
        /// so the solver evaluates every permutation in a single run against the shared mesh.
        /// A case with no scenarios yields one "Base" scenario from its base voltages; each
        /// scenario carries one <see cref="Excitation"/> per electrode terminal, resolved
        /// through <see cref="ElectrostatCase.EffectiveVoltages"/> (scenario overrides plus
        /// nested-static-ring inheritance). Terminals without a resolved voltage default to 0 V.
        /// </summary>
        private static void AddScenarios(BuildContext ctx)
        {
            var problem = ctx.Problem;
            var _case = ctx.Case;

            // No authored permutations: solve once from the base voltages.
            var scenarios = _case.Scenarios is { Count: > 0 }
                ? _case.Scenarios
                : null;

            void AddScenario(string name, VoltageScenario? scenario)
            {
                var voltages = _case.EffectiveVoltages(scenario);
                var excitations = new List<Excitation>(problem.Terminals.Count);
                foreach (var terminal in problem.Terminals)
                {
                    if (string.IsNullOrEmpty(terminal.Name)) continue;
                    var value = voltages.TryGetValue(terminal.Name, out var v) ? v : 0.0;
                    excitations.Add(new Excitation
                    {
                        Terminal = terminal,
                        Value = value,
                        Floating = false
                    });
                }

                problem.Scenarios.Add(new Scenario
                {
                    Name = name,
                    Excitations = excitations
                });
            }

            problem.Scenarios.Clear();
            if (scenarios == null)
            {
                AddScenario("Base", null);
            }
            else
            {
                foreach (var scenario in scenarios)
                    AddScenario(scenario.Name, scenario);
            }
        }

        /// <summary>Feed the conditioned geometry to gmsh, add distance-based refinement fields, and write the mesh (when requested).</summary>
        private static void GenerateMeshPhase(BuildContext ctx, MeshGenerator gmsh, string? mshOut, double lc, bool generateMesh)
        {
            var geometry = ctx.Geom;
            var domain = ctx.Case.Domain;
            var oil_surf = ctx.OilSurf;
            var refineLoops = ctx.RefineLoops;
            var problem = ctx.Problem;

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
                        if (GeometryConditioner.IsOnDomainEdge(ent, domain)) continue;
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

                problem.MeshPath = mshOut;
            }
        }

    }

}
