using System;
using System.Collections.Generic;
using MeshLib;

namespace electrostat
{
    /// <summary>
    /// Locates points in a curved (high-order) triangle mesh by inverting each element's
    /// geometry map x(xi, eta) with Newton's method.
    /// <para>
    /// A <see cref="TriangleLocator"/> over the corner vertices supplies the starting
    /// element and guess. Near a curved edge, the straight-sided triangle that contains a
    /// point can differ from the curved element that does, so when the inversion lands
    /// outside the reference triangle the search moves to edge neighbours (two rings).
    /// Points between a curved boundary edge and its chord are therefore found when the
    /// boundary bulges into the mesh (e.g. a convex electrode corner, where they are
    /// correctly reported as outside); where it bulges outward they are found only via the
    /// caller's hint element.
    /// </para>
    /// </summary>
    public sealed class CurvedElementLocator
    {
        private const int MaxNewtonIterations = 30;
        // Converged once a Newton step is this small in reference coordinates. Roundoff in
        // the mapped coordinates (hundreds of mm) keeps steps from shrinking much below
        // ~1e-13, so a tighter test can report false failures.
        private const double StepTolerance = 1e-10;
        private const double ReferenceTolerance = 1e-9;
        private const int SearchRings = 2;

        private readonly ElementField _geometry;
        private readonly Dictionary<int, List<int>> _neighbors = new();

        public CurvedElementLocator(GmshFile mesh, ElementField geometry)
        {
            _geometry = geometry ?? throw new ArgumentNullException(nameof(geometry));

            // Edge-sharing neighbours from corner connectivity.
            var edgeOwners = new Dictionary<(int, int), int>();
            foreach (var element in mesh.Elements)
            {
                int id = (int)element.Id;
                _neighbors[id] = new List<int>(3);
                for (int k = 0; k < 3; k++)
                {
                    int a = element.Nodes[k], b = element.Nodes[(k + 1) % 3];
                    var edge = a < b ? (a, b) : (b, a);
                    if (edgeOwners.Remove(edge, out int other))
                    {
                        _neighbors[id].Add(other);
                        _neighbors[other].Add(id);
                    }
                    else
                    {
                        edgeOwners[edge] = id;
                    }
                }
            }
        }

        /// <summary>
        /// Find the curved element containing (x, y), starting from
        /// <paramref name="startElementId"/> with initial reference guess (xi0, eta0).
        /// </summary>
        public bool TryLocate(int startElementId, double x, double y, double xi0, double eta0,
            out int elementId, out double xi, out double eta)
        {
            var visited = new HashSet<int> { startElementId };
            var ring = new List<int> { startElementId };
            for (int depth = 0; depth <= SearchRings && ring.Count > 0; depth++)
            {
                var next = new List<int>();
                foreach (int candidate in ring)
                {
                    double guessXi = candidate == startElementId ? xi0 : 1.0 / 3;
                    double guessEta = candidate == startElementId ? eta0 : 1.0 / 3;
                    if (TryInvert(candidate, x, y, guessXi, guessEta, out xi, out eta) && IsInside(xi, eta))
                    {
                        elementId = candidate;
                        return true;
                    }
                    foreach (int neighbor in _neighbors[candidate])
                    {
                        if (visited.Add(neighbor))
                            next.Add(neighbor);
                    }
                }
                ring = next;
            }

            elementId = 0;
            xi = eta = 0;
            return false;
        }

        /// <summary>Solve x(xi, eta) = (x, y) in one element.</summary>
        public bool TryInvert(int elementId, double x, double y, double xi0, double eta0,
            out double xi, out double eta)
        {
            Span<double> p = stackalloc double[2];
            Span<double> pXi = stackalloc double[2];
            Span<double> pEta = stackalloc double[2];
            xi = xi0;
            eta = eta0;

            for (int iteration = 0; iteration < MaxNewtonIterations; iteration++)
            {
                _geometry.Evaluate(elementId, xi, eta, p);
                double rx = p[0] - x, ry = p[1] - y;

                _geometry.EvaluateReferenceGradient(elementId, xi, eta, pXi, pEta);
                double j00 = pXi[0], j10 = pXi[1], j01 = pEta[0], j11 = pEta[1];

                double det = j00 * j11 - j01 * j10;
                if (det == 0 || !double.IsFinite(det)) return false;
                double dXi = (j11 * rx - j01 * ry) / det;
                double dEta = (-j10 * rx + j00 * ry) / det;
                xi -= dXi;
                eta -= dEta;

                // Far outside the reference triangle the map is meaningless; stop.
                if (Math.Abs(xi) > 4 || Math.Abs(eta) > 4) return false;
                if (Math.Abs(dXi) + Math.Abs(dEta) < StepTolerance) return true;
            }
            return false;
        }

        private static bool IsInside(double xi, double eta) =>
            xi >= -ReferenceTolerance && eta >= -ReferenceTolerance && xi + eta <= 1 + ReferenceTolerance;
    }
}
