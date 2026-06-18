using System.Collections.Generic;
using System.Linq;
using System.Text.Json.Serialization;

namespace electrostat
{
    /// <summary>
    /// Top-level transformer definition. Owns the geometry shared by every <see cref="Cut"/>
    /// (windings and their nested static rings, pressboards, angle rings), the interphase
    /// insulation used when an adjacent phase is modeled, the base voltages and voltage
    /// scenarios, and the core/window dimensions needed to place a mirrored adjacent phase.
    /// </summary>
    /// <param name="CoreLegRadius">Radius of the core leg (mm). Half the spacing term used to mirror the adjacent phase.</param>
    /// <param name="WindowWidth">Core window width between adjacent legs (mm).</param>
    /// <param name="InterphaseBarriers">
    /// Pressboard barriers that exist only between phases; added (and then mirrored) when a
    /// cut models the adjacent phase.
    /// </param>
    /// <param name="InterphaseAngleRings">
    /// Angle rings that exist only between phases; added (and then mirrored) when a cut
    /// models the adjacent phase.
    /// </param>
    /// <param name="Cuts">The slices of this transformer's geometry to build / solve.</param>
    public sealed record Transformer(
        string Name,
        double CoreLegRadius,
        double WindowWidth,
        IReadOnlyList<WindingBlock> Windings,
        IReadOnlyList<PressboardBarrier> Pressboards,
        IReadOnlyList<AngleRing> AngleRings,
        IReadOnlyList<StaticRing> StaticRings,
        IReadOnlyList<PressboardBarrier> InterphaseBarriers,
        IReadOnlyList<AngleRing> InterphaseAngleRings,
        Dictionary<string, double> Voltages,
        IReadOnlyList<VoltageScenario>? Scenarios,
        IReadOnlyList<Cut> Cuts)
    {
        /// <summary>
        /// Radial coordinate used to mirror the adjacent phase. A component at radial extent
        /// [r, r+w] is mirrored to [<c>value</c> - r - w, <c>value</c> - r], i.e. reflected
        /// about the window centerline at <c>value/2 = CoreLegRadius + WindowWidth/2</c>.
        /// Equals the legacy <c>rAdjphCL</c> = 2·CoreLegRadius + WindowWidth.
        /// </summary>
        [JsonIgnore]
        public double AdjacentPhaseMirror => 2.0 * CoreLegRadius + WindowWidth;

        /// <summary>
        /// Flatten this transformer for a single <paramref name="cut"/> into the
        /// <see cref="ElectrostatCase"/> the geometry builder / solver consume. The shared
        /// components are reused as-is for a single-phase cut; when the cut models the
        /// adjacent phase the interphase insulation is added and every component is mirrored
        /// about the window centerline (the mirrored electrodes are seeded to 0 V).
        /// </summary>
        public ElectrostatCase ResolveCut(Cut cut)
        {
            // Fresh working copies so mirroring never mutates the shared transformer lists.
            var windings = new List<WindingBlock>(Windings);
            var pressboards = new List<PressboardBarrier>(Pressboards);
            var angleRings = new List<AngleRing>(AngleRings);
            var staticRings = new List<StaticRing>(StaticRings);
            var voltages = new Dictionary<string, double>(Voltages);

            if (cut.IncludeAdjacentPhase)
            {
                // Interphase insulation exists only in the two-phase model. It is added before
                // mirroring so it, too, is reflected to the far side of the window.
                pressboards.AddRange(InterphaseBarriers);
                angleRings.AddRange(InterphaseAngleRings);

                double rAdjphCL = AdjacentPhaseMirror;

                foreach (var wdg in windings.ToArray())
                {
                    var adj = wdg with { Name = wdg.Name + "_2", R0 = rAdjphCL - (wdg.R0 + wdg.Width) };
                    windings.Add(adj);
                    // Mirrored adjacent-phase windings are grounded.
                    voltages[adj.Name] = 0.0;
                }

                foreach (var pb in pressboards.ToArray())
                {
                    var adjPb = pb with { Name = pb.Name + "_2", R0 = rAdjphCL - (pb.R0 + pb.Thickness) };
                    // A taper sloped on the inner edge faces the outer edge once reflected.
                    if (adjPb.TaperBottom.HasValue)
                    {
                        string newSide = adjPb.TaperBottom.Value.Side == "inner" ? "outer" : "inner";
                        adjPb = adjPb with { TaperBottom = adjPb.TaperBottom.Value with { Side = newSide } };
                    }
                    if (adjPb.TaperTop.HasValue)
                    {
                        string newSide = adjPb.TaperTop.Value.Side == "inner" ? "outer" : "inner";
                        adjPb = adjPb with { TaperTop = adjPb.TaperTop.Value with { Side = newSide } };
                    }
                    pressboards.Add(adjPb);
                }

                foreach (var ar in angleRings.ToArray())
                {
                    angleRings.Add(ar with { Name = ar.Name + "_2", R0 = rAdjphCL - ar.R0, Wh = -ar.Wh });
                }

                foreach (var sr in staticRings.ToArray())
                {
                    var adj = sr with
                    {
                        Name = sr.Name + "_2",
                        R0 = rAdjphCL - (sr.R0 + sr.Width),
                        RTL = sr.RTR,
                        RTR = sr.RTL,
                        RBL = sr.RBR,
                        RBR = sr.RBL,
                        ParentWinding = string.IsNullOrEmpty(sr.ParentWinding) ? null : sr.ParentWinding + "_2"
                    };
                    staticRings.Add(adj);
                    // Mirrored static-ring metals are grounded.
                    voltages[adj.Name + "_Metal"] = 0.0;
                }
            }

            return new ElectrostatCase(
                cut.Name,
                cut.Domain,
                windings,
                pressboards,
                angleRings,
                staticRings,
                voltages,
                Scenarios,
                cut.GeometryType);
        }
    }
}
