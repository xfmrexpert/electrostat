using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using MeshLib;
using PureHDF;

namespace electrostat
{
    /// <summary>
    /// Reads the per-scenario fields of an MFEM-ElectroMag field-analysis HDF5 archive
    /// (schema version 2; see MFEM-ElectroMag's docs/coupling_hdf5.md) into
    /// <see cref="FieldSolution"/>s.
    /// <para>
    /// The archive stores native finite-element coefficients, not values at mesh nodes.
    /// When a field's <c>values</c> dataset names its space (<c>space</c> attribute →
    /// <c>/spaces/&lt;collection&gt;</c>), the space supplies each element's DOF list and the
    /// reference positions of those DOFs, which is everything needed to evaluate a nodal
    /// field of any order exactly. Archives written before the solver exported spaces
    /// fall back to a hard-coded reading of the low-order collections: H1 is reduced to
    /// its vertex values (linear in each element — inexact above order 1), and L2 orders
    /// 0 and 1 are read exactly.
    /// </para>
    /// <para>
    /// Only straight-sided triangle meshes are supported, which is what electrostat builds.
    /// </para>
    /// </summary>
    public static class Hdf5FieldResultsReader
    {
        private const int SupportedSchemaVersion = 2;
        private const int TriangleGeometry = 2;   // MFEM Geometry::TRIANGLE
        private const int GmshTriangleType = 2;   // Gmsh 3-node triangle
        private const int OrderingByNodes = 0;    // MFEM Ordering::byNODES
        private const string TriangleNodesDataset = "2"; // reference_nodes/<MFEM geometry code>

        // MFEM's default L2 basis (GaussLegendre) places the order-1 triangle nodes at
        // (a, a), (b, a), (a, b), where a = op0 / w, b = op1 / w, op = the open
        // Gauss-Legendre points on [0, 1], and w = 2*op0 + op1.
        private static readonly double GaussLegendre0 = 0.5 * (1.0 - 1.0 / Math.Sqrt(3.0));
        private static readonly double GaussLegendre1 = 0.5 * (1.0 + 1.0 / Math.Sqrt(3.0));
        private static readonly double L2P1NodeA = GaussLegendre0 / (2.0 * GaussLegendre0 + GaussLegendre1);
        private static readonly double L2P1NodeB = GaussLegendre1 / (2.0 * GaussLegendre0 + GaussLegendre1);

        /// <summary>
        /// Read every scenario in the archive, in solve order. All scenarios share one
        /// <see cref="GmshFile"/> mesh instance.
        /// </summary>
        public static List<FieldSolution> Read(string fileName)
        {
            if (!File.Exists(fileName))
                throw new FileNotFoundException("FEM results file not found.", fileName);

            using var file = H5File.OpenRead(fileName);

            int schemaVersion = file.Attribute("schema_version").Read<int>();
            if (schemaVersion != SupportedSchemaVersion)
                throw new InvalidDataException(
                    $"Unsupported results schema_version {schemaVersion} (expected {SupportedSchemaVersion}).");

            string physicsType = file.Attribute("physics_type").Read<string>();
            if (!string.Equals(physicsType, "electrostatics", StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException($"Expected electrostatics results, found '{physicsType}'.");

            var meshGroup = file.Group("mesh");
            var mesh = ReadMesh(meshGroup, out int vertexCount, out int elementCount);
            var spaces = new Dictionary<string, Space>(StringComparer.Ordinal);

            var solutions = new List<FieldSolution>();
            if (!file.LinkExists("scenarios"))
                return solutions;

            // Scenario groups are named scenario_000000, scenario_000001, ... in solve order.
            var scenarioGroups = file.Group("scenarios").Children()
                .OfType<IH5Group>()
                .OrderBy(g => g.Name, StringComparer.Ordinal);

            foreach (var scenarioGroup in scenarioGroups)
            {
                var solution = new FieldSolution
                {
                    ResultsFile = fileName,
                    ScenarioName = scenarioGroup.AttributeExists("name")
                        ? scenarioGroup.Attribute("name").Read<string>()
                        : scenarioGroup.Name,
                    Mesh = mesh,
                };

                if (scenarioGroup.LinkExists("fields"))
                {
                    foreach (var fieldGroup in scenarioGroup.Group("fields").Children().OfType<IH5Group>())
                        ReadField(file, fieldGroup, mesh, vertexCount, elementCount, spaces, solution);
                }

                solutions.Add(solution);
            }

            return solutions;
        }

        private static GmshFile ReadMesh(IH5Group meshGroup, out int vertexCount, out int elementCount)
        {
            int dimension = meshGroup.Attribute("dimension").Read<int>();
            int spaceDimension = meshGroup.Attribute("space_dimension").Read<int>();
            if (dimension != 2)
                throw new InvalidDataException($"Expected a 2D mesh, found dimension {dimension}.");

            var coordinates = meshGroup.Dataset("vertices").Read<double[]>();
            vertexCount = coordinates.Length / spaceDimension;

            var elements = meshGroup.Group("elements");
            var offsets = elements.Dataset("offsets").Read<int[]>();
            var connectivity = elements.Dataset("vertices").Read<int[]>();
            var attributes = elements.Dataset("attributes").Read<int[]>();
            var geometry = elements.Dataset("geometry").Read<int[]>();
            elementCount = attributes.Length;

            var mesh = new GmshFile();
            for (int v = 0; v < vertexCount; v++)
            {
                double x = coordinates[v * spaceDimension];
                double y = spaceDimension > 1 ? coordinates[v * spaceDimension + 1] : 0.0;
                mesh.Nodes.Add(new GmshNode((uint)(v + 1), x, y, 0.0));
            }

            for (int e = 0; e < elementCount; e++)
            {
                if (geometry[e] != TriangleGeometry || offsets[e + 1] - offsets[e] != 3)
                    throw new InvalidDataException(
                        $"Element {e} is not a triangle (MFEM geometry {geometry[e]}); only triangle meshes are supported.");

                var nodes = new List<int>(3);
                for (int k = offsets[e]; k < offsets[e + 1]; k++)
                    nodes.Add(connectivity[k] + 1);
                mesh.Elements.Add(new GmshElement((uint)(e + 1), GmshTriangleType, new List<int> { attributes[e] }, nodes));
            }

            return mesh;
        }

        /// <summary>A scalar FE space: per-element DOF lists (CSR) and the basis they index.</summary>
        private sealed record Space(int[] Offsets, int[] Dofs, LagrangeTriangleBasis Basis);

        private static void ReadField(IH5Group file, IH5Group fieldGroup, GmshFile mesh, int vertexCount,
            int elementCount, Dictionary<string, Space> spaces, FieldSolution solution)
        {
            var dataset = fieldGroup.Dataset("values");
            var values = dataset.Read<double[]>();
            string collection = dataset.Attribute("finite_element_collection").Read<string>();
            int vdim = dataset.Attribute("vector_dimension").Read<int>();
            bool byNodes = dataset.Attribute("ordering").Read<int>() == OrderingByNodes;
            string name = fieldGroup.Name;

            Space? space;
            if (dataset.AttributeExists("space"))
            {
                string path = dataset.Attribute("space").Read<string>();
                if (!spaces.TryGetValue(path, out space))
                    spaces[path] = space = ReadSpace(file.Group(path), path, elementCount);
            }
            else
            {
                if (!spaces.TryGetValue(collection, out space))
                {
                    space = LegacySpace(collection, mesh, elementCount);
                    if (space != null) spaces[collection] = space;
                }
            }

            // Unknown representation: skip it rather than fail the whole load, so a solver
            // that starts exporting an extra field does not break existing results.
            if (space == null) return;

            int maxDof = space.Dofs.Length == 0 ? -1 : space.Dofs.Max(d => d < 0 ? -1 - d : d);
            if ((maxDof + 1) * vdim > values.Length)
                throw new InvalidDataException(
                    $"Field '{name}' has {values.Length} values; its space indexes DOF {maxDof} with {vdim} components.");

            var field = new ElementField(name, space.Basis, space.Offsets, space.Dofs, values, vdim, byNodes);
            solution.Fields[name] = field;

            // Corner-value views for drawing: continuous fields per vertex, discontinuous
            // ones per element corner.
            Span<double> result = stackalloc double[vdim];
            ReadOnlySpan<(double Xi, double Eta)> corners = [(0, 0), (1, 0), (0, 1)];
            bool continuous = collection.StartsWith("H1_", StringComparison.Ordinal) && vdim == 1;
            var nodal = continuous ? new Dictionary<int, double>(vertexCount) : null;
            var perElement = continuous ? null : new Dictionary<int, ElementNodeField>(elementCount);
            foreach (var element in mesh.Elements)
            {
                int id = (int)element.Id;
                var samples = continuous ? null : new double[3 * vdim];
                for (int n = 0; n < 3; n++)
                {
                    field.Evaluate(id, corners[n].Xi, corners[n].Eta, result);
                    if (continuous)
                        nodal![element.Nodes[n]] = result[0];
                    else
                        result.CopyTo(samples.AsSpan(n * vdim, vdim));
                }
                if (!continuous)
                    perElement![id] = new ElementNodeField(vdim, 3, samples!);
            }
            if (continuous)
                solution.NodalScalars[name] = nodal!;
            else
                solution.ElementNodalFields[name] = perElement!;
        }

        private static Space ReadSpace(IH5Group group, string path, int elementCount)
        {
            var offsets = group.Dataset("element_dof_offsets").Read<int[]>();
            var dofs = group.Dataset("element_dofs").Read<int[]>();
            if (offsets.Length != elementCount + 1)
                throw new InvalidDataException(
                    $"Space '{path}' lists {offsets.Length - 1} elements; the mesh has {elementCount}.");

            var nodesGroup = group.Group("reference_nodes");
            if (!nodesGroup.LinkExists(TriangleNodesDataset))
                throw new InvalidDataException(
                    $"Space '{path}' has no nodal triangle basis (its elements are not nodal or not value-mapped).");
            var nodes = nodesGroup.Dataset(TriangleNodesDataset).Read<double[]>();
            return new Space(offsets, dofs, new LagrangeTriangleBasis(nodes));
        }

        /// <summary>
        /// The space of an archive that does not describe its spaces, for the collections
        /// whose layout is fixed by MFEM conventions alone, or null for any other.
        /// </summary>
        private static Space? LegacySpace(string collection, GmshFile mesh, int elementCount)
        {
            var offsets = new int[elementCount + 1];
            if (collection.StartsWith("H1_2D_P", StringComparison.Ordinal))
            {
                // MFEM numbers H1 vertex DOFs first, by vertex index. The edge and interior
                // DOFs depend on MFEM's internal edge numbering, which is not in the
                // archive, so only the vertex values are used.
                var dofs = new int[3 * elementCount];
                int e = 0;
                foreach (var element in mesh.Elements)
                {
                    for (int n = 0; n < 3; n++)
                        dofs[3 * e + n] = element.Nodes[n] - 1;
                    offsets[++e] = 3 * e;
                }
                return new Space(offsets, dofs, new LagrangeTriangleBasis([0, 0, 1, 0, 0, 1]));
            }

            if (collection == "L2_2D_P0" || collection == "L2_2D_P1")
            {
                int perElement = collection == "L2_2D_P0" ? 1 : 3;
                var dofs = Enumerable.Range(0, perElement * elementCount).ToArray();
                for (int e = 0; e <= elementCount; e++)
                    offsets[e] = perElement * e;
                double[] nodes = perElement == 1
                    ? [1.0 / 3, 1.0 / 3]
                    : [L2P1NodeA, L2P1NodeA, L2P1NodeB, L2P1NodeA, L2P1NodeA, L2P1NodeB];
                return new Space(offsets, dofs, new LagrangeTriangleBasis(nodes));
            }

            return null;
        }
    }
}
