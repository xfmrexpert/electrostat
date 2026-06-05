using System.Collections.Generic;
using electrostat;

namespace electrostat_UI.ViewModels
{
    /// <summary>
    /// One row of the streamline stress summary table. Captures the governing
    /// cumulative-method ("Weidmann inhomogeneous-field") margin of a single
    /// <see cref="StreamlineWithMargin"/> in a flattened, display-friendly form.
    /// </summary>
    public sealed class StreamlineSummaryRow
    {
        /// <summary>1-based ordinal used to identify the streamline in the table.</summary>
        public int Index { get; init; }

        public string Name => $"#{Index}";

        /// <summary>
        /// Cumulative average stress E_average at the governing point of the worst oil
        /// gap, in kV/mm. This (not the local peak |E|) is what the cumulative method
        /// compares against the withstand curve.
        /// </summary>
        public double PeakField { get; init; }

        /// <summary>Withstand E_design at the governing running distance, in kV/mm.</summary>
        public double AllowableField { get; init; }

        /// <summary>
        /// Safety factor at the governing point (E_design / E_average). Values &gt; 1 are
        /// safe, 1.0 is exactly at the limit. Returns +∞ where no oil constraint applies.
        /// </summary>
        public double Margin =>
            PeakField > 0 ? AllowableField / PeakField : double.PositiveInfinity;

        /// <summary>Display-friendly margin: a safety factor, or "n/a" where no limit applies.</summary>
        public string MarginText =>
            double.IsPositiveInfinity(AllowableField) || double.IsNaN(Margin)
                ? "n/a"
                : double.IsPositiveInfinity(Margin) ? "∞" : $"{Margin:F2}×";

        /// <summary>Maximum local |E| seen anywhere along the streamline, in kV/mm.</summary>
        public double MaxField { get; init; }

        /// <summary>Total integrated potential drop along the line in kV (~ V_start - V_end).</summary>
        public double TotalDrop { get; init; }

        /// <summary>Total arc length of the streamline, in mesh units (mm).</summary>
        public double Length { get; init; }

        /// <summary>Number of contiguous single-material runs along the line.</summary>
        public int SegmentCount { get; init; }

        /// <summary>Reason the trace terminated (e.g. HitConductor).</summary>
        public string Termination { get; init; } = string.Empty;

        public double PeakX { get; init; }

        public double PeakY { get; init; }

        public string PeakLocation => $"({PeakX:F1}, {PeakY:F1})";

        /// <summary>
        /// Builds a row from a computed <see cref="StreamlineWithMargin"/>. The governing
        /// oil gap is the one with the worst (minimum) safety margin (E_design/E_average);
        /// its governing point supplies the reported stress, withstand and margin. Lines
        /// with no oil gap fall back to reporting the maximum local |E| with no constraint.
        /// </summary>
        public static StreamlineSummaryRow FromStreamline(int index, StreamlineWithMargin s)
        {
            OilGapCumulativeMargin? governing = null;
            foreach (var g in s.OilGaps)
            {
                if (governing == null || g.GoverningMargin < governing.GoverningMargin)
                    governing = g;
            }

            bool hasGap = governing != null;

            return new StreamlineSummaryRow
            {
                Index = index,
                PeakField = hasGap ? governing!.GoverningEAverage : 0,
                AllowableField = hasGap ? governing!.GoverningEDesign : double.PositiveInfinity,
                MaxField = s.Stress.MaxE,
                TotalDrop = s.Stress.TotalIntegralEdL,
                Length = s.Stress.TotalLength,
                SegmentCount = s.Stress.Segments.Count,
                Termination = s.Line.TerminationReason.ToString(),
                PeakX = hasGap ? governing!.MaxLocalEX : 0,
                PeakY = hasGap ? governing!.MaxLocalEY : 0,
            };
        }
    }
}
