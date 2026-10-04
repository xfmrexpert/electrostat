using System;
using System.Collections.Generic;
using MeshLib;

namespace electrostat
{
    /// <summary>
    /// One scenario's electrostatic field as loaded from the MFEM-ElectroMag HDF5 results
    /// archive (see <see cref="Hdf5FieldResultsReader"/>). Replaces TfmrLib's former
    /// <c>FEMSolution</c>, keeping the same shape so the sampler, results view, and
    /// streamline code are unchanged.
    /// <para>
    /// The solver's mesh is presented as an in-memory <see cref="GmshFile"/> of linear
    /// triangles: node id = MFEM vertex index + 1, element id = MFEM element index + 1,
    /// and <c>Tags[0]</c> = the element's attribute (the physical tag electrostat assigned
    /// when it built the mesh).
    /// </para>
    /// </summary>
    public class FieldSolution
    {
        /// <summary>Path of the HDF5 archive the results were loaded from.</summary>
        public string? ResultsFile { get; init; }

        /// <summary>Scenario name as recorded in the archive.</summary>
        public string? ScenarioName { get; init; }

        /// <summary>The final solved mesh (refined, when AMR ran).</summary>
        public GmshFile? Mesh { get; init; }

        /// <summary>
        /// The element geometry of a curved (high-order) mesh: node coordinates as a
        /// 2-component field, so x(xi, eta) is evaluated like any other field. Null for a
        /// straight-sided mesh, whose elements are the affine maps of their corners.
        /// </summary>
        public ElementField? Geometry { get; init; }

        /// <summary>
        /// Every field at full order (e.g. "V", "E", "Permittivity"), keyed by field name.
        /// Use these for point evaluation; the corner-value views below are for drawing.
        /// </summary>
        public Dictionary<string, ElementField> Fields { get; init; }
            = new(StringComparer.OrdinalIgnoreCase);

        /// <summary>
        /// Continuous nodal scalar fields (e.g. "V") at the mesh vertices, keyed by field
        /// name, then by node id.
        /// </summary>
        public Dictionary<string, Dictionary<int, double>> NodalScalars { get; init; }
            = new(StringComparer.OrdinalIgnoreCase);

        /// <summary>
        /// Per-element (discontinuous) fields (e.g. "E", "Permittivity") sampled at each
        /// element's corner nodes, preserving discontinuities across material interfaces.
        /// Indexed as [fieldName][elementId].
        /// </summary>
        public Dictionary<string, Dictionary<int, ElementNodeField>> ElementNodalFields { get; init; }
            = new(StringComparer.OrdinalIgnoreCase);

        /// <summary>
        /// Physical (material / boundary) tag → human-readable name. The archive carries
        /// no names, so callers populate this from the regions / BC names known at build time.
        /// </summary>
        public Dictionary<int, string> PhysicalNames { get; init; } = new();

        public bool TryGetPotential(out Dictionary<int, double> potential)
        {
            foreach (var key in new[] { "V", "Phi", "Potential" })
            {
                if (NodalScalars.TryGetValue(key, out var v))
                {
                    potential = v;
                    return true;
                }
            }
            potential = default!;
            return false;
        }
    }

    /// <summary>
    /// A finite-element field over a triangle mesh with a nodal (Lagrange) basis of any
    /// order: each element's DOFs are the field's values at the basis nodes, which
    /// <see cref="LagrangeTriangleBasis"/> turns into shape functions.
    /// </summary>
    public sealed class ElementField
    {
        private readonly int[] _offsets;
        private readonly int[] _dofs;
        private readonly double[] _values;
        private readonly int _scalarDofs;
        private readonly bool _byNodes;
        private readonly LagrangeTriangleBasis _basis;

        /// <param name="offsets">CSR offsets into <paramref name="dofs"/>, one per element plus a sentinel.</param>
        /// <param name="dofs">
        /// Scalar DOF indices per element, in the basis's node order. A negative entry d
        /// means DOF -1-d with its sign flipped (MFEM's convention).
        /// </param>
        /// <param name="values">Coefficients for all components.</param>
        /// <param name="components">Vector dimension.</param>
        /// <param name="byNodes">
        /// MFEM ordering: true (byNODES) stores component c of DOF d at
        /// values[c * scalarDofs + d], false (byVDIM) at values[d * components + c].
        /// </param>
        public ElementField(string name, LagrangeTriangleBasis basis, int[] offsets, int[] dofs,
            double[] values, int components, bool byNodes)
        {
            Name = name;
            Components = components;
            _basis = basis;
            _offsets = offsets;
            _dofs = dofs;
            _values = values;
            _scalarDofs = values.Length / components;
            _byNodes = byNodes;
        }

        public string Name { get; }

        public int Components { get; }

        /// <summary>Polynomial order within an element.</summary>
        public int Order => _basis.Order;

        public int ElementCount => _offsets.Length - 1;

        /// <summary>
        /// Evaluate the field in element <paramref name="elementId"/> (1-based, as in
        /// <see cref="FieldSolution.Mesh"/>) at reference coordinates (xi, eta), i.e. the
        /// barycentric weights of element vertices 1 and 2.
        /// </summary>
        public void Evaluate(int elementId, double xi, double eta, Span<double> result)
        {
            int first = ElementDofs(elementId, out int count);
            Span<double> shape = stackalloc double[count];
            _basis.Evaluate(xi, eta, shape);
            Combine(first, shape, result);
        }

        /// <summary>
        /// Derivatives of each component with respect to the reference coordinates
        /// (xi, eta) in element <paramref name="elementId"/>.
        /// </summary>
        public void EvaluateReferenceGradient(int elementId, double xi, double eta,
            Span<double> dXi, Span<double> dEta)
        {
            int first = ElementDofs(elementId, out int count);
            Span<double> shapeXi = stackalloc double[count];
            Span<double> shapeEta = stackalloc double[count];
            _basis.EvaluateGradient(xi, eta, shapeXi, shapeEta);
            Combine(first, shapeXi, dXi);
            Combine(first, shapeEta, dEta);
        }

        private int ElementDofs(int elementId, out int count)
        {
            int element = elementId - 1;
            int first = _offsets[element];
            count = _offsets[element + 1] - first;
            if (count != _basis.Count)
                throw new InvalidOperationException(
                    $"Element {elementId} of field '{Name}' has {count} DOFs; its basis has {_basis.Count}.");
            return first;
        }

        /// <summary>result[c] = sum_k sign_k * value(dof_k, c) * weights[k].</summary>
        private void Combine(int first, ReadOnlySpan<double> weights, Span<double> result)
        {
            for (int c = 0; c < Components; c++)
            {
                double sum = 0;
                for (int k = 0; k < weights.Length; k++)
                {
                    int dof = _dofs[first + k];
                    double sign = 1;
                    if (dof < 0)
                    {
                        dof = -1 - dof;
                        sign = -1;
                    }
                    double value = _byNodes ? _values[c * _scalarDofs + dof] : _values[dof * Components + c];
                    sum += sign * value * weights[k];
                }
                result[c] = sum;
            }
        }
    }

    /// <summary>
    /// One element's worth of per-node field samples. <see cref="Values"/> is component-major:
    /// for node i and component c, the value is Values[i * NumComponents + c].
    /// </summary>
    public readonly record struct ElementNodeField(int NumComponents, int NumNodes, double[] Values)
    {
        public double Get(int localNode, int component) => Values[localNode * NumComponents + component];
    }
}
