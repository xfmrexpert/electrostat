using System;
using System.Collections.Generic;

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
        /// Allowable design field strength (kV/mm) for the given material name and
        /// gap length (mm). Returns <see cref="double.PositiveInfinity"/> if the
        /// material is unknown (so it does not constrain the margin).
        /// </summary>
        double AllowableField(string? materialName, double gapLengthMm);
    }

    /// <summary>
    /// A single Weidmann-style design curve of the form <c>Ed = A · d^B</c>, giving
    /// the allowable design field strength <c>Ed</c> (kV/mm) as a function of oil-gap
    /// length <c>d</c> (mm). The exponent <c>B</c> is negative, so the allowable field
    /// decreases with increasing gap length.
    /// </summary>
    public sealed record DesignCurve(string Name, double A, double B)
    {
        /// <summary>
        /// Allowable design field (kV/mm) for a gap length <paramref name="dMm"/> (mm).
        /// The gap is floored at 1e-6 mm to avoid the singularity at d = 0.
        /// </summary>
        public double Ed(double dMm)
        {
            double d = Math.Max(dMm, 1e-6);
            return A * Math.Pow(d, B);
        }

        public override string ToString() => Name;
    }

    /// <summary>
    /// Standard Weidmann oil-gap design curves. Constants are expressed for the
    /// allowable field <c>Ed</c> in kV/mm with the gap length <c>d</c> in mm
    /// (<c>Ed = A · d^B</c>).
    /// </summary>
    public static class WeidmannCurves
    {
        public static readonly DesignCurve DegasedInsulated   = new("Degased/Insulated", 21.2, -0.36);
        public static readonly DesignCurve SaturatedInsulated = new("Gas-Sat/Insulated", 19.0, -0.38);
        public static readonly DesignCurve DegasedBare        = new("Degased/Bare",      17.8, -0.36);
        public static readonly DesignCurve SaturatedBare      = new("Gas-Sat/Bare",      14.2, -0.36);

        /// <summary>Common creep curve default (from creep.py).</summary>
        public static readonly DesignCurve CreepDefault       = new("Creep Default",     16.6, -0.46);

        /// <summary>Default design curve: Gas-Sat/Insulated.</summary>
        public static readonly DesignCurve Default = SaturatedInsulated;

        /// <summary>All named curves, in display order.</summary>
        public static readonly IReadOnlyList<DesignCurve> All = new[]
        {
            DegasedInsulated, SaturatedInsulated, DegasedBare, SaturatedBare, CreepDefault,
        };
    }

    /// <summary>
    /// <see cref="IDesignCurves"/> implementation backed by a single
    /// <see cref="DesignCurve"/>. Defaults to the Gas-Sat/Insulated Weidmann curve.
    /// The curves apply only to oil gaps; for solid insulation (pressboard, paper)
    /// and metals the allowable field is reported as <see cref="double.PositiveInfinity"/>
    /// so those points do not constrain the Weidmann margin.
    /// </summary>
    public sealed class DefaultWiedmannCurves : IDesignCurves
    {
        private readonly DesignCurve _curve;

        /// <summary>Uses the default Gas-Sat/Insulated curve.</summary>
        public DefaultWiedmannCurves() : this(WeidmannCurves.Default)
        {
        }

        /// <summary>Uses the supplied design curve for oil gaps.</summary>
        public DefaultWiedmannCurves(DesignCurve curve)
            => _curve = curve ?? throw new ArgumentNullException(nameof(curve));

        /// <summary>The design curve used for oil gaps.</summary>
        public DesignCurve Curve => _curve;

        public double AllowableField(string? materialName, double gapLengthMm)
        {
            // Weidmann oil-gap design curves only apply to mineral oil. For solid
            // insulation (pressboard, paper) and metals, return +∞ so the margin
            // calculator reports a stress fraction of 0 (i.e. "no Weidmann
            // constraint at this point"). Per-material qualification of solids
            // requires different design data (Boning/Weidmann solid curves) that
            // is out of scope for this overlay.
            if (gapLengthMm <= 0) return double.PositiveInfinity;
            string m = (materialName ?? string.Empty).ToLowerInvariant();

            if (m.Contains("oil"))
            {
                // DesignCurve.Ed returns kV/mm, matching the sampled |E| (also kV/mm).
                return _curve.Ed(gapLengthMm);
            }

            return double.PositiveInfinity;
        }
    }
}
