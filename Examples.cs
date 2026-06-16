using System.Collections.Generic;
using TfmrLib.FEM;

namespace electrostat
{
    /// <summary>
    /// Factory for the example transformer originally exercised in <c>Program.Main</c>.
    /// </summary>
    public static class Examples
    {
        public static IReadOnlyList<Transformer> All(bool withLVCornerAngle = true)
        {
            var transformers = new List<Transformer>();

            var (windings, pressboards, angleRings, staticRings) = BaseComponents(withLVCornerAngle);

            double rHVOuter = 1120.0 / 2 + 3.0;
            double topZ = 65.0 + 1238.0 + 32.0 + 10.0 + 3.0 + 24.0 + 26.0 + 55.0 + 38.0;

            // Core / window dimensions that locate the mirrored adjacent phase. The legacy
            // mirror constant rAdjphCL = 510 + 655 decomposes as 2*CoreLegRadius + WindowWidth,
            // so CoreLegRadius = 255 (510/2) and WindowWidth = 655.
            double coreLegRadius = 510.0 / 2;
            double windowWidth = 655.0;

            Dictionary<string, double> voltages = new Dictionary<string, double>();
            voltages["HV"] = 230.0e3;
            voltages["SR_TOP_HV_inner_Metal"] = 230.0e3;
            voltages["SR_TOP_HV_outer_Metal"] = 230.0e3;
            voltages["LV"] = 0.0;
            voltages["RV"] = 0.0;
            voltages["Core"] = 0.0;
            voltages["TopYoke"] = 0.0;
            voltages["Tank"] = 0.0;

            // Demo voltage permutations: each energizes a different terminal so dielectric
            // stress can be evaluated for several impulse / service conditions in one solve.
            // Only winding voltages are listed; nested static rings inherit their parent
            // winding (HV rings follow "HV"), and walls stay at their base values.
            var scenarios = new List<VoltageScenario>
            {
                new VoltageScenario("Switching Impulse",
                    new Dictionary<string, double> { ["HV"] = 460.0e3/1.8, ["LV"] = 0.0, ["RV"] = 0.0 }),
                new VoltageScenario("HV impulse",
                    new Dictionary<string, double> { ["HV"] = 550.0e3/2.3, ["LV"] = 0.0, ["RV"] = 0.0 }),
                new VoltageScenario("LV impulse",
                    new Dictionary<string, double> { ["HV"] = 0.0, ["LV"] = 110.0e3/2.3, ["RV"] = 0.0 }),
            };

            // Interphase insulation: present only when a cut models the adjacent phase. These
            // are mirrored along with the rest of the geometry by Transformer.ResolveCut.
            var interphaseBarriers = new List<PressboardBarrier>
            {
                new PressboardBarrier("PB_phase_barrier_1",
                    R0: 1120.0 / 2 + 3.0 + 13.5, ZBottom: -39.0, Thickness: 3.0,
                    Height: 36.0 + 1296.0 + 28.0 + 100.0 + 39.0),
            };

            var interphaseAngleRings = new List<AngleRing>
            {
                new AngleRing("AR_interphase_1", R0: 576.5,
                    ZCorner: 65.0 + 1238.0 + 32.0 + 10.0 + 3.0 + 24.0,
                    Tv: 4.0, Hv: -124.0, Th: 4.0, Wh: -110.5, InsideFilletR: 4.0),
            };

            // The cuts: four single-phase axisymmetric slices that differ only by domain
            // bounds, plus a two-phase window cut solved as a planar problem.
            var cuts = new List<Cut>
            {
                new Cut(
                    "Window Cut (no adjacent phase)",
                    new Domain(RInner: 510.0 / 2, ROuter: 672.0, ZLower: 1000.0, ZUpper: topZ),
                    GeometryType.Axisymmetric),
                new Cut(
                    "Tank Cut - LV",
                    new Domain(RInner: 510.0 / 2, ROuter: rHVOuter + 80.0, ZLower: 1000.0, ZUpper: topZ),
                    GeometryType.Axisymmetric),
                new Cut(
                    "Tank Cut - PA",
                    new Domain(RInner: 510.0 / 2, ROuter: rHVOuter + 400.0, ZLower: 1000.0, ZUpper: topZ + 510.0),
                    GeometryType.Axisymmetric),
                new Cut(
                    "Tank Cut - End",
                    new Domain(RInner: 510.0 / 2, ROuter: 672.0, ZLower: 1000.0, ZUpper: topZ + 510.0),
                    GeometryType.Axisymmetric),
                new Cut(
                    "Window Cut (with adjacent phase)",
                    new Domain(RInner: 510.0 / 2, ROuter: 510.0 / 2 + 655.0, ZLower: 1000.0, ZUpper: topZ),
                    GeometryType.Planar,
                    IncludeAdjacentPhase: true),
            };

            transformers.Add(new Transformer(
                "Example Transformer",
                coreLegRadius,
                windowWidth,
                windings,
                pressboards,
                angleRings,
                staticRings,
                interphaseBarriers,
                interphaseAngleRings,
                voltages,
                scenarios,
                cuts));

            return transformers;
        }

        public static (List<WindingBlock>, List<PressboardBarrier>, List<AngleRing>, List<StaticRing>)
            BaseComponents(bool withLVCornerAngle)
        {
            var windings = new List<WindingBlock>
            {
                new WindingBlock("HV", R0: 886.0/2, ZBottom: 65.0, Width: 109.0, Height: 1238.0, FilletR: 1.0),
                new WindingBlock("LV", R0: 620.0/2, ZBottom: 36.0, Width: 93.0, Height: 1296.0, FilletR: 1.0),
                new WindingBlock("RV", R0: 544.0/2, ZBottom: 0.0, Width: 10.0, Height: 1368.0-30.0, FilletR: 1.0),
            };

            var pressboards = new List<PressboardBarrier>
            {
                new PressboardBarrier("PB_inner", R0: 526.0/2, ZBottom: 0.0, Thickness: 3.0, Height: 1368.0+30.0+82.0),
                new PressboardBarrier("PB_between_RV_LV", R0: 602.0/2, ZBottom: 0.0, Thickness: 4.0, Height: 36.0+1296.0+33.0),
                new PressboardBarrier("PB_between_LV_HV_1", R0: 818.0/2, ZBottom: 0.0, Thickness: 2.0, Height: 36.0+1296.0+28.0),
                new PressboardBarrier("PB_between_LV_HV_2", R0: 840.0/2, ZBottom: 20.0, Thickness: 3.0, Height: 65.0+1238.0+32.0+10.0+3.0+2.0-20.0, TaperTop: new Taper(122.0, 1.8, "outer")),
                new PressboardBarrier("PB_between_LV_HV_3", R0: 868.0/2, ZBottom: 65.0, Thickness: 3.0, Height: 1238.0, TaperTop: new Taper(88.0, 0.8, "inner")),
                new PressboardBarrier("PB_outer", R0: 1120.0/2, ZBottom: 65.0, Thickness: 3.0, Height: 1250.0),
                new PressboardBarrier("PB_RV_taper_ring", R0: 544.0/2, ZBottom: 1368.0-30.0, Thickness: 10.0, Height: 30.0),
                new PressboardBarrier("PB_HV_washer_top", R0: 840.0/2+1.0, ZBottom: 65.0+1238.0+32.0+10.0+3.0+24.0, Thickness: 145.0, Height: 4.0),
                new PressboardBarrier("PB_press_ring", R0: 544.0/2, ZBottom: 65.0+1238.0+32.0+10.0+3.0+24.0+26.0, Thickness: 300.0, Height: 55.0),
                new PressboardBarrier("PB_HV_SR_spacer", R0: 868.0/2+3.0+4.0, ZBottom: 65.0+1238.0+2.0, Thickness: 117.0, Height: 4.0),
            };

            var angleRings = new List<AngleRing>
            {
                new AngleRing("AR_HV_lower", R0: 868.0/2-1.0, ZCorner: 65.0+1238.0+32.0,
                    Tv: 3.0, Hv: -120.0, Th: 3.0, Wh: 129.0, InsideFilletR: 15.0,
                    TaperVTip: new Taper(88.0, 1.0, "inner")),
                new AngleRing("AR_HV_upper", R0: 840.0/2+2.0, ZCorner: 65.0+1238.0+32.0+10.0+3.0,
                    Tv: 3.0, Hv: -120.0, Th: 3.0, Wh: 145.0, InsideFilletR: 15.0,
                    TaperVTip: new Taper(101.9, 1.0, "outer")),
                new AngleRing("AR_HV_corner", R0: 886.0/2-1.0, ZCorner: 65.0+1238.0+1.0,
                    Tv: 1.0, Hv: -10.0, Th: 1.0, Wh: 10.0, InsideFilletR: 1.0)
            };

            if (withLVCornerAngle)
            {
                angleRings.Add(new AngleRing("AR_LV_corner", R0: 620.0/2 + 93.0 + 1.0,
                    ZCorner: 36.0 + 1296.0 + 1.0, Tv: 1.0, Hv: -10.0, Th: 1.0, Wh: -10.0,
                    InsideFilletR: 1.0));
            }

            var staticRings = new List<StaticRing>
            {
                new StaticRing("SR_TOP_HV_inner", R0: 884.0/2, ZBottom: 65.0+1238.0+6.0+3.0,
                    Width: 45.0, Height: 12.0, RTL: 10.0, RTR: 2.0, RBR: 2.0, RBL: 2.0, TPaper: 3.0,
                    ParentWinding: "HV"),
                new StaticRing("SR_TOP_HV_outer", R0: 884.0/2+66.0, ZBottom: 65.0+1238.0+6.0+3.0,
                    Width: 45.0, Height: 12.0, RTL: 2.0, RTR: 10.0, RBR: 2.0, RBL: 2.0, TPaper: 3.0,
                    ParentWinding: "HV")
            };

            return (windings, pressboards, angleRings, staticRings);
        }
    }
}
