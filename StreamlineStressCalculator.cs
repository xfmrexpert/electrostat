using System;
using System.Collections.Generic;

namespace electrostat
{
    /// <summary>
    /// One contiguous run along a <see cref="Streamline"/> in which the field samples
    /// share a single material (physical) tag.
    /// </summary>
    /// <param name="MaterialTag">Gmsh physical tag of the run's elements (0 if unknown).</param>
    /// <param name="Length">Arc length of the run, in mesh units.</param>
    /// <param name="StartArcLength">Arc length at the start of the run, measured along the streamline.</param>
    /// <param name="EndArcLength">Arc length at the end of the run.</param>
    /// <param name="IntegralEdL">∫|E|·dl over the run (kV, when |E| is kV/mm and length is mm).</param>
    /// <param name="MaxE">Maximum |E| sample within the run (kV/mm).</param>
    /// <param name="MeanE">Length-weighted mean |E| over the run (kV/mm), equal to <see cref="IntegralEdL"/>/<see cref="Length"/>.</param>
    public readonly record struct StreamlineSegment(
        int MaterialTag,
        double Length,
        double StartArcLength,
        double EndArcLength,
        double IntegralEdL,
        double MaxE,
        double MeanE);

    /// <summary>
    /// Aggregate stress / margin metrics over an entire streamline, grouped by material.
    /// </summary>
    public sealed class StreamlineStress
    {
        /// <summary>Per-material runs in traversal order. Adjacent runs differ in material tag.</summary>
        public required IReadOnlyList<StreamlineSegment> Segments { get; init; }

        /// <summary>Aggregate length per material tag (sum of <see cref="StreamlineSegment.Length"/> for that tag).</summary>
        public required IReadOnlyDictionary<int, double> LengthByMaterial { get; init; }

        /// <summary>Aggregate ∫|E|·dl per material tag (kV).</summary>
        public required IReadOnlyDictionary<int, double> IntegralEdLByMaterial { get; init; }

        /// <summary>Maximum |E| seen anywhere on the streamline (kV/mm).</summary>
        public required double MaxE { get; init; }

        /// <summary>Total integrated potential drop along the streamline in kV (≈ V_start − V_end).</summary>
        public required double TotalIntegralEdL { get; init; }

        /// <summary>Total arc length.</summary>
        public required double TotalLength { get; init; }
    }

    /// <summary>
    /// Computes per-material segmentation and stress metrics along a <see cref="Streamline"/>.
    /// </summary>
    /// <remarks>
    /// The streamline points record the containing element's physical tag at each sample
    /// (see <see cref="StreamlinePoint.MaterialTag"/>). This service walks consecutive
    /// points, integrating |E| with the trapezoidal rule against the arc-length increment
    /// and breaking the trace into runs whenever the material tag changes.
    ///
    /// Material transitions occur at the *midpoint* of the step that crosses the
    /// interface, since either endpoint's reported tag is equally valid; the contributions
    /// to the two sides cancel so the total ∫|E|·dl is unaffected.
    /// </remarks>
    public static class StreamlineStressCalculator
    {
        public static StreamlineStress Compute(Streamline line)
        {
            if (line is null) throw new ArgumentNullException(nameof(line));
            var pts = line.Points;
            var segments = new List<StreamlineSegment>();
            var lengthByMat = new Dictionary<int, double>();
            var integralByMat = new Dictionary<int, double>();
            double maxE = 0;
            double totalEdL = 0;
            double totalLen = 0;

            if (pts.Count < 2)
            {
                return new StreamlineStress
                {
                    Segments = segments,
                    LengthByMaterial = lengthByMat,
                    IntegralEdLByMaterial = integralByMat,
                    MaxE = pts.Count == 1 ? pts[0].EMagnitude : 0,
                    TotalIntegralEdL = 0,
                    TotalLength = 0,
                };
            }

            // Running accumulators for the current material run.
            int curTag = pts[0].MaterialTag;
            double curStartS = pts[0].ArcLength;
            double curLen = 0;
            double curEdL = 0;
            double curMaxE = pts[0].EMagnitude;
            if (curMaxE > maxE) maxE = curMaxE;

            for (int i = 1; i < pts.Count; i++)
            {
                var a = pts[i - 1];
                var b = pts[i];
                double ds = b.ArcLength - a.ArcLength;
                if (ds < 0) ds = 0;

                double eA = a.EMagnitude;
                double eB = b.EMagnitude;
                if (eB > maxE) maxE = eB;

                if (a.MaterialTag == b.MaterialTag)
                {
                    // Same material on both sides: full trapezoid contributes to current run.
                    double dEdL = 0.5 * (eA + eB) * ds;
                    curLen += ds;
                    curEdL += dEdL;
                    if (eB > curMaxE) curMaxE = eB;

                    totalLen += ds;
                    totalEdL += dEdL;
                }
                else
                {
                    // Material change across this step: split it at the midpoint.
                    double halfDs = 0.5 * ds;
                    double midE = 0.5 * (eA + eB);
                    double leftEdL  = 0.5 * (eA + midE) * halfDs;
                    double rightEdL = 0.5 * (midE + eB) * halfDs;

                    // Close out the current run.
                    curLen += halfDs;
                    curEdL += leftEdL;
                    if (midE > curMaxE) curMaxE = midE;
                    AppendRun(segments, lengthByMat, integralByMat,
                        curTag, curStartS, curLen, curEdL, curMaxE);

                    // Start a new run on b's material.
                    curTag = b.MaterialTag;
                    curStartS = a.ArcLength + halfDs;
                    curLen = halfDs;
                    curEdL = rightEdL;
                    curMaxE = Math.Max(midE, eB);

                    totalLen += ds;
                    totalEdL += leftEdL + rightEdL;
                }
            }

            // Flush the final run.
            AppendRun(segments, lengthByMat, integralByMat,
                curTag, curStartS, curLen, curEdL, curMaxE);

            return new StreamlineStress
            {
                Segments = segments,
                LengthByMaterial = lengthByMat,
                IntegralEdLByMaterial = integralByMat,
                MaxE = maxE,
                TotalIntegralEdL = totalEdL,
                TotalLength = totalLen,
            };
        }

        private static void AppendRun(
            List<StreamlineSegment> segments,
            Dictionary<int, double> lengthByMat,
            Dictionary<int, double> integralByMat,
            int tag, double startS, double len, double edL, double maxE)
        {
            if (len <= 0) return;
            segments.Add(new StreamlineSegment(
                MaterialTag: tag,
                Length: len,
                StartArcLength: startS,
                EndArcLength: startS + len,
                IntegralEdL: edL,
                MaxE: maxE,
                MeanE: edL / len));

            lengthByMat[tag] = (lengthByMat.TryGetValue(tag, out var lcur) ? lcur : 0) + len;
            integralByMat[tag] = (integralByMat.TryGetValue(tag, out var icur) ? icur : 0) + edL;
        }
    }
}
