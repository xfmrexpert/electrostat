using System;
using System.Collections.Generic;

namespace electrostat
{
    /// <summary>
    /// One sample of stress / margin along a streamline, suitable for color-mapping
    /// a polyline overlay.
    /// </summary>
    /// <param name="X">World X coordinate.</param>
    /// <param name="Y">World Y coordinate.</param>
    /// <param name="EMagnitude">Local |E| at this point (kV/mm).</param>
    /// <param name="EAllow">
    /// Governing withstand E_design (kV/mm) for the oil gap that contains this point,
    /// from the cumulative method (constant within a gap). +∞ for metals / solids /
    /// unknown materials, which carry no Weidmann constraint.
    /// </param>
    /// <param name="Margin">
    /// Governing cumulative safety margin E_design/E_average for the gap that contains
    /// this point (constant within a gap; +∞ where no oil constraint applies). This is
    /// the value the overlay color-maps, so a whole gap reads as one margin.
    /// </param>
    public readonly record struct StreamlineMarginPoint(
        double X, double Y, double EMagnitude, double EAllow, double Margin);

    /// <summary>
    /// A streamline annotated with per-point safety margins and a global worst-case
    /// (minimum) margin, ready for the UI to render as a colored polyline.
    /// </summary>
    public sealed class StreamlineWithMargin
    {
        public required Streamline Line { get; init; }
        public required IReadOnlyList<StreamlineMarginPoint> Points { get; init; }

        /// <summary>
        /// Worst-case (minimum) safety margin across all oil gaps on the line. +∞ when
        /// the line has no oil gap (no Weidmann constraint applies).
        /// </summary>
        public required double MinMargin { get; init; }
        public required StreamlineStress Stress { get; init; }

        /// <summary>
        /// Per-oil-gap cumulative-method margin results along the streamline. Empty for
        /// lines with no oil gaps. The streamline's governing margin
        /// (<see cref="MinMargin"/>) is the worst gap's
        /// <see cref="OilGapCumulativeMargin.GoverningMargin"/>.
        /// </summary>
        public required IReadOnlyList<OilGapCumulativeMargin> OilGaps { get; init; }
    }

    /// <summary>
    /// Cumulative ("Weidmann inhomogeneous-field") margin result for one contiguous oil
    /// gap along a streamline. The method (per the Transformers-Academy lesson) is:
    /// <list type="number">
    /// <item>build a descending "falling" field function E_falling(d) (max stress at d=0);</item>
    /// <item>integrate it into the cumulative average stress
    /// E_average(d) = (1/d)·∫₀ᵈ E_falling dd';</item>
    /// <item>compute the safety margin σ(d) = E_design(d) / E_average(d), where the
    /// withstand E_design is evaluated at the <em>running distance d</em> (not the full
    /// gap length); the governing margin is min σ over 0&lt;d≤gap length.</item>
    /// </list>
    /// All arrays are aligned and ordered by the falling rearrangement: index 0 is the
    /// highest-stress sub-length. Distances are in mm; fields in kV/mm.
    /// </summary>
    public sealed class OilGapCumulativeMargin
    {
        /// <summary>Running cumulative distance d (mm) at each falling-order step (0&lt;d≤<see cref="GapLength"/>).</summary>
        public required IReadOnlyList<double> Distance { get; init; }

        /// <summary>Falling field E_falling: the gap's per-segment |E| in descending order (kV/mm).</summary>
        public required IReadOnlyList<double> EFalling { get; init; }

        /// <summary>Cumulative average stress E_average(d) = (1/d)·∫₀ᵈ E_falling dd' (kV/mm).</summary>
        public required IReadOnlyList<double> EAverage { get; init; }

        /// <summary>Withstand E_design(d) evaluated at each running distance d (kV/mm).</summary>
        public required IReadOnlyList<double> EDesign { get; init; }

        /// <summary>Cumulative safety margin σ(d) = E_design(d)/E_average(d) at each d (governing = min).</summary>
        public required IReadOnlyList<double> Margin { get; init; }

        /// <summary>Total oil-gap arc length (mm).</summary>
        public required double GapLength { get; init; }

        /// <summary>Arc length (mm) from the streamline start to the beginning of this gap.</summary>
        public required double ArcStart { get; init; }

        /// <summary>Material (physical) name used to look up the design curve.</summary>
        public required string MaterialName { get; init; }

        /// <summary>Governing (worst, minimum) safety margin E_design/E_average over the gap.</summary>
        public required double GoverningMargin { get; init; }

        /// <summary>Running distance d (mm) at the governing point (where σ is smallest).</summary>
        public required double GoverningDistance { get; init; }

        /// <summary>Cumulative average stress E_average at the governing point (kV/mm).</summary>
        public required double GoverningEAverage { get; init; }

        /// <summary>Withstand E_design at the governing distance (kV/mm).</summary>
        public required double GoverningEDesign { get; init; }

        /// <summary>Maximum local |E| anywhere in the gap (kV/mm).</summary>
        public required double MaxLocalE { get; init; }

        /// <summary>World X of the maximum local |E| in the gap.</summary>
        public required double MaxLocalEX { get; init; }

        /// <summary>World Y of the maximum local |E| in the gap.</summary>
        public required double MaxLocalEY { get; init; }

        /// <summary>Index of the first streamline point in this gap's physical run (inclusive).</summary>
        public required int PointStart { get; init; }

        /// <summary>Index one past the last streamline point in this gap's physical run (exclusive).</summary>
        public required int PointEnd { get; init; }
    }

    /// <summary>
    /// Computes cumulative-method ("Weidmann inhomogeneous-field") stress margins along
    /// a streamline, per the Transformers-Academy lesson. For every contiguous oil gap
    /// the local field profile is rearranged into a descending "falling" function, that
    /// function is integrated into a cumulative <em>average</em> stress
    /// E_average(d) = (1/d)·∫₀ᵈ E_falling dd', and the safety margin is
    /// σ(d) = E_design(d)/E_average(d) with the withstand curve evaluated at the running
    /// distance d (the governing margin is the smallest σ over the gap). Non-oil runs
    /// (solids / metals) carry no constraint.
    /// </summary>
    public static class StreamlineMarginCalculator
    {
        public static StreamlineWithMargin Compute(
            Streamline line,
            IReadOnlyDictionary<int, string> physicalNames,
            IDesignCurves curves)
        {
            if (line is null) throw new ArgumentNullException(nameof(line));
            if (physicalNames is null) throw new ArgumentNullException(nameof(physicalNames));
            if (curves is null) throw new ArgumentNullException(nameof(curves));

            var stress = StreamlineStressCalculator.Compute(line);

            var pts = line.Points;
            int n = pts.Count;
            var marginPts = new List<StreamlineMarginPoint>(n);
            var oilGaps = new List<OilGapCumulativeMargin>();
            double minMargin = double.PositiveInfinity;

            // Cumulative *Euclidean* arc length per point. The streamline is stitched
            // from a backward + forward trace, so the per-point ArcLength field is not
            // monotonic across the join; integrating from coordinates keeps distances
            // well-defined for both the cumulative method and gap labeling.
            var arc = new double[n];
            for (int k = 1; k < n; k++)
            {
                double dx = pts[k].X - pts[k - 1].X;
                double dy = pts[k].Y - pts[k - 1].Y;
                arc[k] = arc[k - 1] + Math.Sqrt(dx * dx + dy * dy);
            }

            // Which oil gap (if any) each point belongs to, so per-point overlay values
            // can be set to that gap's governing margin after the gaps are built.
            var pointGap = new int[n];
            for (int k = 0; k < n; k++) pointGap[k] = -1;

            // Walk contiguous same-material runs; build a cumulative margin for oil runs.
            int i = 0;
            while (i < n)
            {
                int tag = pts[i].MaterialTag;
                int runStart = i;
                while (i < n && pts[i].MaterialTag == tag) i++;
                int runEnd = i; // exclusive

                physicalNames.TryGetValue(tag, out var rawName);
                string name = rawName ?? string.Empty;
                if (!IsOil(curves, name) || runEnd - runStart < 2)
                    continue;

                // Sub-segments fully inside the oil run: ds from coordinates, field is
                // the segment-average |E|. Track the gap's peak local |E| for reporting.
                var ds = new List<double>(runEnd - runStart);
                var es = new List<double>(runEnd - runStart);
                double maxLocalE = pts[runStart].EMagnitude;
                double maxLocalEX = pts[runStart].X, maxLocalEY = pts[runStart].Y;
                for (int k = runStart; k < runEnd; k++)
                {
                    double e = pts[k].EMagnitude;
                    if (e > maxLocalE) { maxLocalE = e; maxLocalEX = pts[k].X; maxLocalEY = pts[k].Y; }
                }
                for (int k = runStart + 1; k < runEnd; k++)
                {
                    double dx = pts[k].X - pts[k - 1].X;
                    double dy = pts[k].Y - pts[k - 1].Y;
                    double seglen = Math.Sqrt(dx * dx + dy * dy);
                    if (seglen <= 0) continue;
                    ds.Add(seglen);
                    es.Add(0.5 * (pts[k - 1].EMagnitude + pts[k].EMagnitude));
                }
                if (ds.Count == 0) continue;

                // Step 1: falling function — rearrange the gap's field profile to be
                // descending (max stress at d = 0). Sort sub-segments by |E| desc.
                //var order = new int[ds.Count];
                //for (int j = 0; j < order.Length; j++) order[j] = j;
                //Array.Sort(order, (x, y) => es[y].CompareTo(es[x]));

                // Step 1: Nelson's Contiguous Expansion
                // Find the segment with the maximum local field to act as the seed.
                int m = ds.Count;
                var order = new int[m];
                int maxIdx = 0;
                double maxE = -1;
                for (int j = 0; j < m; j++)
                {
                    if (es[j] > maxE)
                    {
                        maxE = es[j];
                        maxIdx = j;
                    }
                }

                // Initialize the growing contiguous region
                int leftIdx = maxIdx;
                int rightIdx = maxIdx;
                order[0] = maxIdx;

                double currentLength = ds[maxIdx];
                double currentIntegral = es[maxIdx] * ds[maxIdx];

                // Iteratively grow outward along the streamline path
                for (int step = 1; step < m; step++)
                {
                    int candidateLeft = leftIdx - 1;
                    int candidateRight = rightIdx + 1;

                    bool canGoLeft = candidateLeft >= 0;
                    bool canGoRight = candidateRight < m;

                    int chosenIdx = -1;

                    if (canGoLeft && !canGoRight)
                    {
                        chosenIdx = candidateLeft;
                        leftIdx--;
                    }
                    else if (!canGoLeft && canGoRight)
                    {
                        chosenIdx = candidateRight;
                        rightIdx++;
                    }
                    else
                    {
                        // Nelson Step (d): Examine cumulative stress in either direction
                        // and choose the path extension yielding the higher cumulative stress.
                        double stressIfLeft = (currentIntegral + es[candidateLeft] * ds[candidateLeft]) /
                                              (currentLength + ds[candidateLeft]);

                        double stressIfRight = (currentIntegral + es[candidateRight] * ds[candidateRight]) /
                                               (currentLength + ds[candidateRight]);

                        if (stressIfLeft >= stressIfRight)
                        {
                            chosenIdx = candidateLeft;
                            leftIdx--;
                        }
                        else
                        {
                            chosenIdx = candidateRight;
                            rightIdx++;
                        }
                    }

                    // Commit the chosen step
                    order[step] = chosenIdx;
                    currentLength += ds[chosenIdx];
                    currentIntegral += es[chosenIdx] * ds[chosenIdx];
                }

                m = ds.Count;
                var dist = new double[m];
                var eFalling = new double[m];
                var eAverage = new double[m];
                var eDesign = new double[m];
                var margin = new double[m];

                // Steps 2 & 3: cumulative average stress and σ(d) = E_design(d)/E_average(d),
                // withstand evaluated at the running distance d. The governing (worst)
                // margin is the smallest σ over the gap (typically at full gap distance).
                double cumLen = 0, cumEdL = 0;
                double govMargin = double.PositiveInfinity;
                int govIdx = 0;
                for (int j = 0; j < m; j++)
                {
                    int s = order[j];
                    cumLen += ds[s];
                    cumEdL += es[s] * ds[s];
                    double avg = cumEdL / cumLen;
                    double des = curves.AllowableField(name, cumLen);
                    double mg = (avg > 0 && des > 0 && !double.IsPositiveInfinity(des) && !double.IsNaN(des))
                        ? des / avg
                        : double.PositiveInfinity;

                    dist[j] = cumLen;
                    eFalling[j] = es[s];
                    eAverage[j] = avg;
                    eDesign[j] = des;
                    margin[j] = mg;
                    if (mg < govMargin) { govMargin = mg; govIdx = j; }
                }

                var gap = new OilGapCumulativeMargin
                {
                    Distance = dist,
                    EFalling = eFalling,
                    EAverage = eAverage,
                    EDesign = eDesign,
                    Margin = margin,
                    GapLength = cumLen,
                    ArcStart = arc[runStart],
                    MaterialName = name,
                    GoverningMargin = govMargin,
                    GoverningDistance = dist[govIdx],
                    GoverningEAverage = eAverage[govIdx],
                    GoverningEDesign = eDesign[govIdx],
                    MaxLocalE = maxLocalE,
                    MaxLocalEX = maxLocalEX,
                    MaxLocalEY = maxLocalEY,
                    PointStart = runStart,
                    PointEnd = runEnd,
                };

                int gapIdx = oilGaps.Count;
                oilGaps.Add(gap);
                for (int k = runStart; k < runEnd; k++) pointGap[k] = gapIdx;
                if (govMargin < minMargin) minMargin = govMargin;
            }

            // Per-point overlay values: a whole oil gap reads as its one governing
            // cumulative margin; non-oil points carry no constraint (margin +∞).
            for (int k = 0; k < n; k++)
            {
                var p = pts[k];
                int gi = pointGap[k];
                if (gi >= 0)
                {
                    var g = oilGaps[gi];
                    marginPts.Add(new StreamlineMarginPoint(
                        p.X, p.Y, p.EMagnitude, g.GoverningEDesign, g.GoverningMargin));
                }
                else
                {
                    marginPts.Add(new StreamlineMarginPoint(
                        p.X, p.Y, p.EMagnitude, double.PositiveInfinity, double.PositiveInfinity));
                }
            }

            return new StreamlineWithMargin
            {
                Line = line,
                Points = marginPts,
                MinMargin = minMargin,
                Stress = stress,
                OilGaps = oilGaps,
            };
        }

        /// <summary>
        /// True when the material carries a finite Weidmann oil-gap design curve (i.e.
        /// the cumulative method applies). Probed at a representative 1 mm gap.
        /// </summary>
        private static bool IsOil(IDesignCurves curves, string name)
        {
            double probe = curves.AllowableField(name, 1.0);
            return probe > 0 && !double.IsPositiveInfinity(probe) && !double.IsNaN(probe);
        }
    }
}
