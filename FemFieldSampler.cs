using System;
using System.Collections.Generic;
using MeshLib;
using TfmrLib.FEM;

namespace electrostat
{
    /// <summary>
    /// <see cref="IFieldSampler"/> backed by an <see cref="FEMSolution"/>. Locates the
    /// containing triangle with a <see cref="TriangleLocator"/> and interpolates fields
    /// using linear (P1) barycentric weights on the three corner nodes.
    /// </summary>
    /// <remarks>
    /// <para>
    /// V is interpolated from the continuous nodal scalar field (typically "V").
    /// </para>
    /// <para>
    /// E is interpolated from <see cref="FEMSolution.ElementNodalFields"/> (typically
    /// "E") so that the discontinuity at material interfaces is preserved: the value
    /// returned for a point in element <c>k</c> is the in-element interpolation, even
    /// when that element shares nodes with one in a different material. For 6-node
    /// (P2) triangles only the three corner samples are used; this is exact for P1
    /// fields and a low-order approximation for P2.
    /// </para>
    /// <para>
    /// A small per-instance hint is cached so successive calls (e.g. from a streamline
    /// integrator) take the fast walk-locate path through the mesh.
    /// </para>
    /// </remarks>
    public sealed class FemFieldSampler : IFieldSampler
    {
        private readonly FEMSolution _solution;
        private readonly TriangleLocator _locator;
        private readonly Dictionary<int, double>? _potential;
        private readonly Dictionary<int, ElementNodeField>? _eField;

        // Mutable hint for sequential queries (not thread-safe; create one sampler per tracer).
        private int _hint;

        public FemFieldSampler(FEMSolution solution)
        {
            _solution = solution ?? throw new ArgumentNullException(nameof(solution));
            if (solution.Mesh == null)
                throw new ArgumentException("FEMSolution has no mesh attached.", nameof(solution));

            _locator = new TriangleLocator(solution.Mesh);

            if (solution.TryGetPotential(out var pot))
                _potential = pot;

            foreach (var key in new[] { "E", "ElectricField", "E_field" })
            {
                if (solution.ElementNodalFields.TryGetValue(key, out var f))
                {
                    _eField = f;
                    break;
                }
            }
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

            if (!_eField.TryGetValue(hit.ElementId, out var field) ||
                field.NumNodes < 3 || field.NumComponents < 2)
            {
                return false;
            }

            // Use the first three (corner) nodal samples with barycentric weights.
            double w0 = hit.B0, w1 = hit.B1, w2 = hit.B2;
            ex = w0 * field.Get(0, 0) + w1 * field.Get(1, 0) + w2 * field.Get(2, 0);
            ey = w0 * field.Get(0, 1) + w1 * field.Get(1, 1) + w2 * field.Get(2, 1);
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

            if (!_potential.TryGetValue(hit.N0, out double v0) ||
                !_potential.TryGetValue(hit.N1, out double v1) ||
                !_potential.TryGetValue(hit.N2, out double v2))
            {
                return false;
            }

            v = hit.B0 * v0 + hit.B1 * v1 + hit.B2 * v2;
            return true;
        }

        /// <summary>Reset the walk-locate hint (use when starting a new trace far from the last point).</summary>
        public void ResetHint() => _hint = 0;
    }
}
