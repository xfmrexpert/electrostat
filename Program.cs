namespace electrostat
{
    internal class Program
    {
        static void Main(string[] args)
        {
            Console.WriteLine("Hello, World!");
           
            foreach (bool withLVCornerAngle in new[] { true })
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
                    new PressboardBarrier("PB_between_LV_HV_3", R0: 868.0/2, ZBottom: 65.0, Thickness: 3.0, Height: 1238.0, TaperTop: new Taper(88.0, 2.0, "inner")),
                    new PressboardBarrier("PB_outer", R0: 1120.0/2, ZBottom: 65.0, Thickness: 3.0, Height: 1250.0),
                    new PressboardBarrier("PB_RV_taper_ring", R0: 544.0/2, ZBottom: 1368.0-30.0, Thickness: 10.0, Height: 30.0),
                    new PressboardBarrier("PB_HV_washer_top", R0: 840.0/2+1.0, ZBottom: 65.0+1238.0+32.0+10.0+3.0+24.0, Thickness: 145.0, Height: 4.0),
                    new PressboardBarrier("PB_press_ring", R0: 544.0/2, ZBottom: 65.0+1238.0+32.0+10.0+3.0+24.0+26.0, Thickness: 300.0, Height: 55.0),
                    new PressboardBarrier("PB_HV_SR_spacer", R0: 868.0/2+3.0+4.0, ZBottom: 65.0+1238.0+2.0, Thickness: 117.0, Height: 4.0),
                };

                var angleRings = new List<AngleRing>
                {
                    new AngleRing(
                        Name: "AR_HV_lower",
                        R0: 868.0/2-1.0,
                        ZCorner: 65.0+1238.0+32.0,
                        Tv: 3.0,
                        Hv: -120.0,
                        Th: 3.0,
                        Wh: 129.0,
                        InsideFilletR: 15.0,
                        TaperVTip: new Taper(88.0, 1.0, "inner")
                    ),
                    new AngleRing(
                        Name: "AR_HV_upper",
                        R0: 840.0/2+2.0,
                        ZCorner: 65.0+1238.0+32.0+10.0+3.0,
                        Tv: 3.0,
                        Hv: -120.0,
                        Th: 3.0,
                        Wh: 145.0,
                        InsideFilletR: 15.0,
                        TaperVTip: new Taper(101.9, 1.0, "outer")
                    ),
                    new AngleRing(
                        Name: "AR_HV_corner",
                        R0: 886.0/2-1.0,
                        ZCorner: 65.0+1238.0+1.0,
                        Tv: 1.0,
                        Hv: -10.0,
                        Th: 1.0,
                        Wh: 10.0,
                        InsideFilletR: 1.0
                    )
                };

                if (withLVCornerAngle)
                {
                    angleRings.Add(new AngleRing(
                        Name: "AR_LV_corner",
                        R0: 620.0 / 2 + 93.0 + 1.0,
                        ZCorner: 36.0 + 1296.0 + 1.0,
                        Tv: 1.0,
                        Hv: -10.0,
                        Th: 1.0,
                        Wh: -10.0,
                        InsideFilletR: 1.0
                    ));
                }

                var staticRings = new List<StaticRing>
                {
                    new StaticRing(
                        Name: "SR_TOP_HV_inner",
                        R0: 884.0/2,
                        ZBottom: 65.0+1238.0+6.0+3.0,
                        Width: 45.0,
                        Height: 12.0,
                        RTL: 10.0, RTR: 2.0, RBR: 2.0, RBL: 2.0,
                        TPaper: 3.0
                    ),
                    new StaticRing(
                        Name: "SR_TOP_HV_outer",
                        R0: 884.0/2+66.0,
                        ZBottom: 65.0+1238.0+6.0+3.0,
                        Width: 45.0,
                        Height: 12.0,
                        RTL: 2.0, RTR: 10.0, RBR: 2.0, RBL: 2.0,
                        TPaper: 3.0
                    )
                };

                double rHVOuter = 1120.0 / 2 + 3.0;

                // Window Cut (w/o adj phase)
                var dom = new Domain(
                    RInner: 510.0 / 2,
                    ROuter: 672.0,
                    ZLower: 1000.0,
                    ZUpper: 65.0 + 1238.0 + 32.0 + 10.0 + 3.0 + 24.0 + 26.0 + 55.0 + 38.0
                );
                string caseName = "geom_core_noadj";
                if (!withLVCornerAngle) caseName += "/noLVcorner";
                GeometryBuilder.BuildModel(dom, windings, pressboards, angleRings, staticRings, lc: 5.0, mshOut: $"{caseName}/geom.msh", verbose: true);
                GeometryBuilder.RunGetDPAnalysis($"{caseName}/geom.msh", $"{caseName}/");

                // Tank Cut - LV
                dom = new Domain(
                    RInner: 510.0 / 2,
                    ROuter: rHVOuter + 80.0,
                    ZLower: 1000.0,
                    ZUpper: 65.0 + 1238.0 + 32.0 + 10.0 + 3.0 + 24.0 + 26.0 + 55.0 + 38.0
                );
                caseName = "geom_tank_LV";
                if (!withLVCornerAngle) caseName += "/noLVcorner";
                GeometryBuilder.BuildModel(dom, windings, pressboards, angleRings, staticRings, lc: 5.0, mshOut: $"{caseName}/geom.msh", verbose: true);
                GeometryBuilder.RunGetDPAnalysis($"{caseName}/geom.msh", $"{caseName}/");

                // Tank Cut - PA
                dom = new Domain(
                    RInner: 510.0 / 2,
                    ROuter: rHVOuter + 400.0,
                    ZLower: 1000.0,
                    ZUpper: 65.0 + 1238.0 + 32.0 + 10.0 + 3.0 + 24.0 + 26.0 + 55.0 + 38.0 + 510.0
                );
                caseName = "geom_tank_PA";
                if (!withLVCornerAngle) caseName += "/noLVcorner";
                GeometryBuilder.BuildModel(dom, windings, pressboards, angleRings, staticRings, lc: 5.0, mshOut: $"{caseName}/geom.msh", verbose: true);
                GeometryBuilder.RunGetDPAnalysis($"{caseName}/geom.msh", $"{caseName}/");

                // Tank Cut - End
                dom = new Domain(
                    RInner: 510.0 / 2,
                    ROuter: 672.0,
                    ZLower: 1000.0,
                    ZUpper: 65.0 + 1238.0 + 32.0 + 10.0 + 3.0 + 24.0 + 26.0 + 55.0 + 38.0 + 510.0
                );
                caseName = "geom_tank_end";
                if (!withLVCornerAngle) caseName += "/noLVcorner";
                GeometryBuilder.BuildModel(dom, windings, pressboards, angleRings, staticRings, lc: 5.0, mshOut: $"{caseName}/geom.msh", verbose: true);
                GeometryBuilder.RunGetDPAnalysis($"{caseName}/geom.msh", $"{caseName}/");

                // Window Cut (w/ adj phase)
                dom = new Domain(
                    RInner: 510.0 / 2,
                    ROuter: 510.0 / 2 + 655.0,
                    ZLower: 1000.0,
                    ZUpper: 65.0 + 1238.0 + 32.0 + 10.0 + 3.0 + 24.0 + 26.0 + 55.0 + 38.0
                );

                pressboards.Add(new PressboardBarrier("PB_phase_barrier_1", R0: 1120.0 / 2 + 3.0 + 13.5, ZBottom: -39.0, Thickness: 3.0, Height: 36.0 + 1296.0 + 28.0 + 100.0 + 39.0));

                // Add angles between phases
                angleRings.Add(new AngleRing(
                    Name: "AR_interphase_1",
                    R0: 576.5,
                    ZCorner: 65.0 + 1238.0 + 32.0 + 10.0 + 3.0 + 24.0,
                    Tv: 4.0,
                    Hv: -124.0,
                    Th: 4.0,
                    Wh: -110.5,
                    InsideFilletR: 4.0
                ));

                double rMidwindow = 510.0 / 2 + 655.0 / 2;
                double dBarrierSpace = rMidwindow - (1120.0 / 2 + 3.0 + 13.5 + 3.0);
                double rAdjphCL = 510.0 + 655.0;

                // Create mirrored adjacent phase windings
                var adjWdgs = new List<WindingBlock>();
                foreach (var wdg in windings.ToList())
                {
                    var adjWdg = wdg with { Name = wdg.Name + "_2", R0 = rAdjphCL - (wdg.R0 + wdg.Width) };
                    adjWdgs.Add(adjWdg);
                }
                windings.AddRange(adjWdgs);

                // Create mirrored adjacent phase pressboards
                var adjPbs = new List<PressboardBarrier>();
                foreach (var pb in pressboards.ToList())
                {
                    var adjPb = pb with { Name = pb.Name + "_2", R0 = rAdjphCL - (pb.R0 + pb.Thickness) };

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

                    adjPbs.Add(adjPb);
                }
                pressboards.AddRange(adjPbs);

                // Create mirrored adjacent phase angle rings
                var adjArs = new List<AngleRing>();
                foreach (var ar in angleRings.ToList())
                {
                    var adjAr = ar with { Name = ar.Name + "_2", R0 = rAdjphCL - ar.R0, Wh = -ar.Wh };
                    adjArs.Add(adjAr);
                }
                angleRings.AddRange(adjArs);

                // Create mirrored adjacent phase static rings
                var adjSrs = new List<StaticRing>();
                foreach (var sr in staticRings.ToList())
                {
                    var adjSr = sr with
                    {
                        Name = sr.Name + "_2",
                        R0 = rAdjphCL - (sr.R0 + sr.Width),
                        RTL = sr.RTR,
                        RTR = sr.RTL,
                        RBL = sr.RBR,
                        RBR = sr.RBL
                    };
                    adjSrs.Add(adjSr);
                }
                staticRings.AddRange(adjSrs);

                caseName = "geom_core_adjph";
                if (!withLVCornerAngle) caseName += "/noLVcorner";
                GeometryBuilder.BuildModel(dom, windings, pressboards, angleRings, staticRings, lc: 5.0, mshOut: $"{caseName}/geom.msh", verbose: true);
                GeometryBuilder.RunGetDPAnalysis($"{caseName}/geom.msh", $"{caseName}/");

                caseName = "geom_core_adjph_planar";
                if (!withLVCornerAngle) caseName += "/noLVcorner";
                GeometryBuilder.BuildModel(dom, windings, pressboards, angleRings, staticRings, lc: 5.0, mshOut: $"{caseName}/geom.msh", verbose: true);
                GeometryBuilder.RunGetDPAnalysis($"{caseName}/geom.msh", $"{caseName}/", proFile: "electrostatics_planar.pro");
            }
        }
    }
}
