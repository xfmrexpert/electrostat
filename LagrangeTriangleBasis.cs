using System;
using MathNet.Numerics.LinearAlgebra;

namespace electrostat
{
    /// <summary>
    /// The nodal (Lagrange) basis of the complete degree-p polynomial space on the
    /// reference triangle (0,0), (1,0), (0,1), defined by an arbitrary set of nodes.
    /// <para>
    /// Shape function k is 1 at node k and 0 at the others, so a field whose DOFs are its
    /// values at those nodes is evaluated as sum_k u_k * phi_k(xi, eta). Built from the
    /// node positions alone, so it reproduces any nodal element (MFEM's Gauss-Lobatto H1,
    /// Gauss-Legendre L2, equispaced, ...) without knowing how the solver placed them.
    /// </para>
    /// <para>
    /// The shape functions are expanded in the orthogonal Proriol-Koornwinder-Dubiner
    /// basis rather than monomials, which keeps the Vandermonde matrix well conditioned
    /// up to the solver's maximum order.
    /// </para>
    /// </summary>
    public sealed class LagrangeTriangleBasis
    {
        /// <summary>Largest supported order; shape-function buffers are stack-allocated.</summary>
        public const int MaxOrder = 10;

        private readonly double[,] _inverseVandermonde;

        public int Order { get; }

        public int Count { get; }

        /// <param name="nodes">Reference node coordinates, flattened as [x0, y0, x1, y1, ...].</param>
        public LagrangeTriangleBasis(double[] nodes)
        {
            Count = nodes.Length / 2;
            Order = OrderForCount(Count);

            var vandermonde = Matrix<double>.Build.Dense(Count, Count);
            Span<double> modes = stackalloc double[Count];
            for (int i = 0; i < Count; i++)
            {
                EvaluateModes(Order, nodes[2 * i], nodes[2 * i + 1], modes);
                for (int j = 0; j < Count; j++)
                    vandermonde[i, j] = modes[j];
            }

            var inverse = vandermonde.Inverse();
            _inverseVandermonde = inverse.ToArray();
        }

        /// <summary>Lagrange shape function values at (xi, eta); <paramref name="shape"/> has length <see cref="Count"/>.</summary>
        public void Evaluate(double xi, double eta, Span<double> shape)
        {
            Span<double> modes = stackalloc double[Count];
            EvaluateModes(Order, xi, eta, modes);

            // phi_k = sum_j Vinv[j, k] * psi_j, so that phi_k(node_i) = (V * Vinv)[i, k] = delta_ik.
            for (int k = 0; k < Count; k++)
            {
                double sum = 0;
                for (int j = 0; j < Count; j++)
                    sum += _inverseVandermonde[j, k] * modes[j];
                shape[k] = sum;
            }
        }

        private static int OrderForCount(int count)
        {
            for (int p = 0; p <= MaxOrder; p++)
            {
                int n = (p + 1) * (p + 2) / 2;
                if (n == count) return p;
                if (n > count) break;
            }
            throw new ArgumentException(
                $"{count} nodes is not the size of a complete triangle polynomial space of order 0..{MaxOrder}.");
        }

        /// <summary>
        /// Proriol-Koornwinder-Dubiner modes psi_ij, i + j &lt;= p, on the collapsed
        /// coordinates (a, b) of the biunit triangle.
        /// </summary>
        private static void EvaluateModes(int order, double xi, double eta, Span<double> modes)
        {
            double r = 2 * xi - 1;
            double s = 2 * eta - 1;
            double a = Math.Abs(1 - s) < 1e-14 ? -1.0 : 2 * (1 + r) / (1 - s) - 1;
            double b = s;
            double collapse = 0.5 * (1 - b);

            Span<double> legendre = stackalloc double[order + 1];
            Span<double> jacobi = stackalloc double[order + 1];
            Jacobi(order, 0, a, legendre);

            int m = 0;
            double collapsePower = 1;
            for (int i = 0; i <= order; i++)
            {
                Jacobi(order - i, 2 * i + 1, b, jacobi);
                for (int j = 0; i + j <= order; j++)
                    modes[m++] = legendre[i] * collapsePower * jacobi[j];
                collapsePower *= collapse;
            }
        }

        /// <summary>Jacobi polynomials P_n^(alpha, 0)(x) for n = 0..maxDegree.</summary>
        private static void Jacobi(int maxDegree, int alpha, double x, Span<double> values)
        {
            values[0] = 1;
            if (maxDegree == 0) return;
            values[1] = 0.5 * ((alpha + 2) * x + alpha);
            for (int n = 1; n < maxDegree; n++)
            {
                double twoNAlpha = 2 * n + alpha;
                double denominator = 2.0 * (n + 1) * (n + alpha + 1) * twoNAlpha;
                double linear = (twoNAlpha + 1) * ((twoNAlpha + 2) * twoNAlpha * x + alpha * alpha);
                double previous = 2.0 * (n + alpha) * n * (twoNAlpha + 2);
                values[n + 1] = (linear * values[n] - previous * values[n - 1]) / denominator;
            }
        }
    }
}
