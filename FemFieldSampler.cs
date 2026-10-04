using System;
using System.Collections.Generic;
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
    /// V is the continuous potential (typically "V"). E is evaluated in the containing
    /// element only, so the discontinuity at material interfaces is preserved: on a
    /// straight-sided mesh from the solver's E field (typically "E"), which is exact
    /// there; on a curved mesh as -grad V through the element's geometry map. Either way
    /// the value returned for a point in element <c>k</c> is element <c>k</c>'s field,
    /// even when that element shares nodes with one in a different material. The returned field is in kV/mm (the raw FEM field is V/mm and is scaled
    /// here).
    /// </para>
    /// <para>
    /// On a straight-sided mesh the locator's barycentric weights B1 and B2 are the
    /// reference coordinates (xi, eta) of the point, because the mesh keeps each element's
    /// vertices in the solver's order. On a curved mesh (<see cref="FieldSolution.Geometry"/>
    /// set) the element map is inverted instead, by <see cref="CurvedElementLocator"/>.
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
        private readonly CurvedElementLocator? _curved;
        private readonly Dictionary<int, int> _elementTags = new();

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

            if (solution.Geometry != null)
            {
                _curved = new CurvedElementLocator(solution.Mesh, solution.Geometry);
                foreach (var element in solution.Mesh.Elements)
                    _elementTags[(int)element.Id] = element.Tags.Count > 0 ? element.Tags[0] : 0;
            }
        }

        /// <summary>
        /// Find the element containing (x, y) and the point's reference coordinates in it.
        /// </summary>
        private bool TryLocate(double x, double y, out int elementId, out int materialTag,
            out double xi, out double eta)
        {
            bool straightHit = _locator.TryLocate(x, y, _hint, out var hit);
            if (_curved == null)
            {
                elementId = hit.ElementId;
                materialTag = hit.PhysicalTag;
                xi = hit.B1;
                eta = hit.B2;
                return straightHit;
            }

            // Curved: start from the straight-sided hit, or from the previous element when
            // the point lies outside every straight triangle (a boundary bulging outward).
            bool found;
            if (straightHit)
            {
                found = _curved.TryLocate(hit.ElementId, x, y, hit.B1, hit.B2, out elementId, out xi, out eta);
            }
            else if (_elementTags.ContainsKey(_hint))
            {
                found = _curved.TryLocate(_hint, x, y, 1.0 / 3, 1.0 / 3, out elementId, out xi, out eta);
            }
            else
            {
                // No previous element to search from (first query, or after ResetHint).
                found = false;
                elementId = 0;
                xi = eta = 0;
            }
            materialTag = found ? _elementTags[elementId] : 0;
            return found;
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

            bool fromPotential = _curved != null && _potential != null;
            if (_eField == null && !fromPotential) return false;
            if (!TryLocate(x, y, out elementId, out materialTag, out double xi, out double eta)) return false;
            _hint = elementId;

            if (fromPotential)
            {
                // On a curved element E is not a polynomial in (xi, eta), so the solver's
                // projected E only approximates it. -grad V is the exact field of the
                // solution: grad_x V = J^-T grad_xi V, J the geometry map's Jacobian.
                Span<double> vXi = stackalloc double[1];
                Span<double> vEta = stackalloc double[1];
                Span<double> xXi = stackalloc double[2];
                Span<double> xEta = stackalloc double[2];
                _potential!.EvaluateReferenceGradient(elementId, xi, eta, vXi, vEta);
                _solution.Geometry!.EvaluateReferenceGradient(elementId, xi, eta, xXi, xEta);
                double j00 = xXi[0], j10 = xXi[1], j01 = xEta[0], j11 = xEta[1];
                double det = j00 * j11 - j01 * j10;
                ex = -(j11 * vXi[0] - j10 * vEta[0]) / det;
                ey = -(-j01 * vXi[0] + j00 * vEta[0]) / det;
            }
            else
            {
                Span<double> e = stackalloc double[_eField!.Components];
                _eField.Evaluate(elementId, xi, eta, e);
                ex = e[0];
                ey = e[1];
            }

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
            if (!TryLocate(x, y, out elementId, out materialTag, out double xi, out double eta)) return false;
            _hint = elementId;

            Span<double> value = stackalloc double[1];
            _potential.Evaluate(elementId, xi, eta, value);
            v = value[0];
            return true;
        }

        /// <summary>Reset the walk-locate hint (use when starting a new trace far from the last point).</summary>
        public void ResetHint() => _hint = 0;
    }
}
