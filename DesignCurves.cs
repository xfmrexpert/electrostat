using System;

namespace electrostat
{
    /// <summary>
    /// Provides allowable design field strength along a transformer-insulation
    /// streamline / oil duct as a function of gap length and material. These are
    /// "Weidmann"-style design curves: E_allow(d) decreases with increasing gap
    /// length d because longer gaps have a higher probability of containing a
    /// statistically-weak point.
    /// </summary>
    public interface IDesignCurves
    {
        /// <summary>
        /// Allowable design field strength (V/mm) for the given material name and
        /// gap length (mm). Returns <see cref="double.PositiveInfinity"/> if the
        /// material is unknown (so it does not constrain the margin).
        /// </summary>
        double AllowableField(string? materialName, double gapLengthMm);
    }

    /// <summary>
    /// Default placeholder implementation. Power-law fits of the form
    /// <c>E_allow = A · d^-n</c> with constants chosen to roughly match
    /// open-literature values for transformer-grade mineral oil and oil-impregnated
    /// pressboard, expressed in V/mm with d in mm.
    /// </summary>
    public sealed class DefaultWiedmannCurves : IDesignCurves
    {
        public double AllowableField(string? materialName, double gapLengthMm)
        {
            // Weidmann oil-gap design curves only apply to mineral oil. For solid
            // insulation (pressboard, paper), return +∞ so the margin
            // calculator reports a stress fraction of 0 (i.e. "no Weidmann
            // constraint at this point"). Per-material qualification of solids
            // requires different design data (Boning/Weidmann solid curves) that
            // is out of scope for this overlay.
            if (gapLengthMm <= 0) return double.PositiveInfinity;
            string m = (materialName ?? string.Empty).ToLowerInvariant();

            if (m.Contains("oil"))
            {
                // Mineral oil (1-min withstand basis). Placeholder fit:
                //   E_allow [V/mm] = A * d^-n,  d in mm
                const double A = 17_900.0;
                const double n = 0.37;
                return A * Math.Pow(gapLengthMm, -n);
            }

            return double.PositiveInfinity;
        }
    }
}
