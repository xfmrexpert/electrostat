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
    /// The archive stores native finite-element coefficients, not values at mesh nodes,
    /// so they are converted here:
    /// <list type="bullet">
    /// <item>H1 primary fields (V): the first <c>NV</c> coefficients are the vertex values
    /// (MFEM numbers vertex DOFs first, by vertex index). Higher-order edge DOFs are
    /// dropped, matching the linear corner interpolation the sampler always used.</item>
    /// <item>L2 derived fields (E, Permittivity): element-local nodal coefficients,
    /// evaluated at each element's three corners. For order 1 the field is linear in the
    /// element, so the corner values reproduce it exactly.</item>
    /// </list>
    /// Only straight-sided triangle meshes are supported, which is what electrostat builds.
    /// </para>
    /// </summary>
    public static class Hdf5FieldResultsReader
    {
        private const int SupportedSchemaVersion = 2;
        private const int TriangleGeometry = 2;   // MFEM Geometry::TRIANGLE
        private const int GmshTriangleType = 2;   // Gmsh 3-node triangle
        private const int OrderingByNodes = 0;    // MFEM Ordering::byNODES

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
                        ReadField(fieldGroup, mesh, vertexCount, elementCount, solution);
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

        private static void ReadField(IH5Group fieldGroup, GmshFile mesh, int vertexCount, int elementCount,
            FieldSolution solution)
        {
            var dataset = fieldGroup.Dataset("values");
            var values = dataset.Read<double[]>();
            string collection = dataset.Attribute("finite_element_collection").Read<string>();
            int vdim = dataset.Attribute("vector_dimension").Read<int>();
            int ordering = dataset.Attribute("ordering").Read<int>();
            int scalarDofs = values.Length / vdim;

            // Component c of DOF d.
            double Coefficient(int dof, int c) =>
                ordering == OrderingByNodes ? values[c * scalarDofs + dof] : values[dof * vdim + c];

            string name = fieldGroup.Name;

            if (collection.StartsWith("H1_2D_P", StringComparison.Ordinal) && vdim == 1)
            {
                if (scalarDofs < vertexCount)
                    throw new InvalidDataException($"Field '{name}' has {scalarDofs} DOFs for {vertexCount} vertices.");

                var nodal = new Dictionary<int, double>(vertexCount);
                for (int v = 0; v < vertexCount; v++)
                    nodal[v + 1] = Coefficient(v, 0);
                solution.NodalScalars[name] = nodal;
                return;
            }

            if (collection == "L2_2D_P0" || collection == "L2_2D_P1")
            {
                int dofsPerElement = collection == "L2_2D_P0" ? 1 : 3;
                if (scalarDofs != elementCount * dofsPerElement)
                    throw new InvalidDataException(
                        $"Field '{name}' has {scalarDofs} DOFs for {elementCount} elements of {dofsPerElement} DOFs each.");

                var perElement = new Dictionary<int, ElementNodeField>(elementCount);
                var corner = new double[3];
                for (int e = 0; e < elementCount; e++)
                {
                    var samples = new double[3 * vdim];
                    for (int c = 0; c < vdim; c++)
                    {
                        if (dofsPerElement == 1)
                        {
                            corner[0] = corner[1] = corner[2] = Coefficient(e, c);
                        }
                        else
                        {
                            int first = e * 3;
                            LinearCornerValues(Coefficient(first, c), Coefficient(first + 1, c),
                                Coefficient(first + 2, c), corner);
                        }
                        for (int n = 0; n < 3; n++)
                            samples[n * vdim + c] = corner[n];
                    }
                    perElement[e + 1] = new ElementNodeField(vdim, 3, samples);
                }
                solution.ElementNodalFields[name] = perElement;
                return;
            }

            // Unknown representation: skip it rather than fail the whole load, so a solver
            // that starts exporting an extra field does not break existing results.
        }

        /// <summary>
        /// Given an order-1 L2 field's values at MFEM's Gauss-Legendre triangle nodes
        /// (a, a), (b, a), (a, b), return its values at the reference corners
        /// (0, 0), (1, 0), (0, 1) — i.e. at element vertices 0, 1, 2.
        /// </summary>
        private static void LinearCornerValues(double f0, double f1, double f2, double[] corner)
        {
            double h = L2P1NodeB - L2P1NodeA;
            double dXi = (f1 - f0) / h;
            double dEta = (f2 - f0) / h;
            double origin = f0 - L2P1NodeA * (dXi + dEta);
            corner[0] = origin;
            corner[1] = origin + dXi;
            corner[2] = origin + dEta;
        }
    }
}
