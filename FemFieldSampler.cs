using System;
using MeshLib;

namespace electrostat
{
    /// <summary>
    /// <see cref="IFieldSampler"/> backed by a <see cref="FieldSolution"/>. Locates the
    /// containing triangle with a <see cref="TriangleLocator"/> and evaluates the fields at
    /// their full polynomial order through <see cref="FieldSolution.Fields"/>.
    /// </summary>
    /// <remarks>
    /// <para>
    /// V is the continuous potential (typically "V"). E (typically "E") is evaluated in
    /// the containing element only, so the discontinuity at material interfaces is
    /// preserved: the value returned for a point in element <c>k</c> is element
    /// <c>k</c>'s polynomial, even when that element shares nodes with one in a different
    /// material. The returned field is in kV/mm (the raw FEM field is V/mm and is scaled
    /// here).
    /// </para>
    /// <para>
    /// The locator's barycentric weights B1 and B2 are the reference coordinates
    /// (xi, eta) of the point, because the mesh keeps each element's vertices in the
    /// solver's order. Exact for straight-sided triangles, which electrostat builds.
    /// </para>
    /// <para>
    /// A small per-instance hint is cached so successive calls (e.g. from a streamline
    /// integrator) take the fast walk-locate path through the mesh.
    /// </para>
    /// </remarks>
    public sealed class FemFieldSampler : IFieldSampler
    {
        /// <summary>Scale factor converting the raw FEM field (V/mm) to kV/mm.</summary>
        private const double VPerMmToKVPerMm = 1e-3;

        private readonly FieldSolution _solution;
        private readonly TriangleLocator _locator;
        private readonly ElementField? _potential;
        private readonly ElementField? _eField;

        // Mutable hint for sequential queries (not thread-safe; create one sampler per tracer).
        private int _hint;

        public FemFieldSampler(FieldSolution solution)
        {
            _solution = solution ?? throw new ArgumentNullException(nameof(solution));
            if (solution.Mesh == null)
                throw new ArgumentException("FieldSolution has no mesh attached.", nameof(solution));

            _locator = new TriangleLocator(solution.Mesh);
            _potential = FindField(solution, 1, "V", "Phi", "Potential");
            _eField = FindField(solution, 2, "E", "ElectricField", "E_field");
        }

        private static ElementField? FindField(FieldSolution solution, int minComponents, params string[] names)
        {
            foreach (var key in names)
            {
                if (solution.Fields.TryGetValue(key, out var f) && f.Components >= minComponents)
                    return f;
            }
            return null;
        }

        /// <summary>The underlying triangle locator (exposed for advanced/testing use).</summary>
        public TriangleLocator Locator => _locator;

        /// <inheritdoc/>
        public bool TryEvaluateE(double x, double y, out double ex, out double ey,
            out int elementId, out int materialTag)
        {
            ex = ey = 0;
            elementId = 0;
            materialTag = 0;

            if (_eField == null) return false;
            if (!_locator.TryLocate(x, y, _hint, out var hit)) return false;

            _hint = hit.ElementId;
            elementId = hit.ElementId;
            materialTag = hit.PhysicalTag;

            Span<double> e = stackalloc double[_eField.Components];
            _eField.Evaluate(hit.ElementId, hit.B1, hit.B2, e);
            ex = e[0];
            ey = e[1];

            // The raw FEM field is V/mm (geometry in mm, potentials in V). Convert to
            // kV/mm here so the whole streamline/stress/margin pipeline works in kV/mm.
            ex *= VPerMmToKVPerMm;
            ey *= VPerMmToKVPerMm;
            return true;
        }

        /// <inheritdoc/>
        public bool TryEvaluateV(double x, double y, out double v,
            out int elementId, out int materialTag)
        {
            v = 0;
            elementId = 0;
            materialTag = 0;

            if (_potential == null) return false;
            if (!_locator.TryLocate(x, y, _hint, out var hit)) return false;

            _hint = hit.ElementId;
            elementId = hit.ElementId;
            materialTag = hit.PhysicalTag;

            Span<double> value = stackalloc double[1];
            _potential.Evaluate(hit.ElementId, hit.B1, hit.B2, value);
            v = value[0];
            return true;
        }

        /// <summary>Reset the walk-locate hint (use when starting a new trace far from the last point).</summary>
        public void ResetHint() => _hint = 0;
    }
}
