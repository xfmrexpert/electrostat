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
    /// <param name="EMagnitude">Local |E| (V/mm).</param>
    /// <param name="EAllow">Allowable |E| at this point (V/mm). May be +∞ for metals/unknown materials.</param>
    /// <param name="StressFraction">|E| / E_allow, clamped at 0 below.</param>
    public readonly record struct StreamlineMarginPoint(
        double X, double Y, double EMagnitude, double EAllow, double StressFraction);

    /// <summary>
    /// A streamline annotated with per-point stress fractions and a global worst-case
    /// fraction, ready for the UI to render as a colored polyline.
    /// </summary>
    public sealed class StreamlineWithMargin
    {
        public required Streamline Line { get; init; }
        public required IReadOnlyList<StreamlineMarginPoint> Points { get; init; }
        public required double MaxStressFraction { get; init; }
        public required StreamlineStress Stress { get; init; }
    }

    /// <summary>
    /// Computes per-point stress fractions along a streamline using an
    /// <see cref="IWiedmannCurves"/> lookup. The "gap length" used for the allowable
    /// field at every sample is the streamline's total arc length, which is the
    /// conventional design basis for an insulation gap.
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

            // The Weidmann curves apply only to oil gaps, and the relevant gap length
            // for each point is the length of the contiguous *oil* run that contains
            // it — not the total streamline length, which would mix in the (much
            // stiffer) solid sections. Build a quick lookup of oil-run length keyed
            // by material tag, falling back to the segment in which a point lives.
            var pts = line.Points;
            var marginPts = new List<StreamlineMarginPoint>(pts.Count);
            double maxFrac = 0;

            // For each point, find its containing segment and use that segment's
            // length as the gap (only meaningful when the segment is oil).
            int segIdx = 0;
            var segs = stress.Segments;
            foreach (var p in pts)
            {
                while (segIdx < segs.Count - 1 && p.ArcLength > segs[segIdx].EndArcLength)
                    segIdx++;

                physicalNames.TryGetValue(p.MaterialTag, out var name);
                double gap = segIdx < segs.Count ? segs[segIdx].Length : 0;
                double eAllow = curves.AllowableField(name, gap);
                double frac = (eAllow > 0 && !double.IsPositiveInfinity(eAllow))
                    ? p.EMagnitude / eAllow
                    : 0;
                if (frac > maxFrac) maxFrac = frac;
                marginPts.Add(new StreamlineMarginPoint(p.X, p.Y, p.EMagnitude, eAllow, frac));
            }

            return new StreamlineWithMargin
            {
                Line = line,
                Points = marginPts,
                MaxStressFraction = maxFrac,
                Stress = stress,
            };
        }
    }
}
