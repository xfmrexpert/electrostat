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
    /// (P2) triangles all six nodal samples are used with the quadratic Lagrange shape
    /// functions, so the result is exact for the P2 field on straight-sided triangles;
    /// 3-node (P1) triangles use the three corner samples. The returned field is in
    /// kV/mm (the raw FEM field is V/mm and is scaled here).
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

            // Area (barycentric) coordinates of the query point. Local node indices 0..2
            // of the element-nodal field align with the locator's corner nodes N0..N2.
            double w0 = hit.B0, w1 = hit.B1, w2 = hit.B2;

            if (field.NumNodes >= 6)
            {
                // Quadratic (P2) Lagrange interpolation using all six nodal samples so the
                // curvature of the field inside the element is honoured (matters most in
                // the high-gradient corners that govern insulation margins). Gmsh 6-node
                // triangle ordering: corners 0,1,2; mid-edge nodes 3 (edge 0-1),
                // 4 (edge 1-2), 5 (edge 2-0). The corner-based area coordinates are exact
                // for straight-sided P2 triangles.
                double n0 = w0 * (2.0 * w0 - 1.0);
                double n1 = w1 * (2.0 * w1 - 1.0);
                double n2 = w2 * (2.0 * w2 - 1.0);
                double n3 = 4.0 * w0 * w1;
                double n4 = 4.0 * w1 * w2;
                double n5 = 4.0 * w2 * w0;

                ex = n0 * field.Get(0, 0) + n1 * field.Get(1, 0) + n2 * field.Get(2, 0)
                   + n3 * field.Get(3, 0) + n4 * field.Get(4, 0) + n5 * field.Get(5, 0);
                ey = n0 * field.Get(0, 1) + n1 * field.Get(1, 1) + n2 * field.Get(2, 1)
                   + n3 * field.Get(3, 1) + n4 * field.Get(4, 1) + n5 * field.Get(5, 1);
            }
            else
            {
                // Linear (P1) interpolation on the three corner samples.
                ex = w0 * field.Get(0, 0) + w1 * field.Get(1, 0) + w2 * field.Get(2, 0);
                ey = w0 * field.Get(0, 1) + w1 * field.Get(1, 1) + w2 * field.Get(2, 1);
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
