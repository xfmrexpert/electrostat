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
        /// Continuous nodal scalar fields (e.g. "V"), keyed by field name, then by node id.
        /// Only the vertex values of the H1 field are kept, so interpolation inside an
        /// element is linear on its corners.
        /// </summary>
        public Dictionary<string, Dictionary<int, double>> NodalScalars { get; init; }
            = new(StringComparer.OrdinalIgnoreCase);

        /// <summary>
        /// Per-element fields (e.g. "E", "Permittivity") sampled at each element's corner
        /// nodes, preserving discontinuities across material interfaces. Indexed as
        /// [fieldName][elementId].
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
    /// One element's worth of per-node field samples. <see cref="Values"/> is component-major:
    /// for node i and component c, the value is Values[i * NumComponents + c].
    /// </summary>
    public readonly record struct ElementNodeField(int NumComponents, int NumNodes, double[] Values)
    {
        public double Get(int localNode, int component) => Values[localNode * NumComponents + component];
    }
}
