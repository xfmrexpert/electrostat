using System;
using System.Numerics;
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
    /// <para>
    /// Derivatives are exact to machine precision: the modes are evaluated as
    /// polynomials, so complex-step differentiation (f'(x) = Im f(x + ih) / h) applies
    /// with a step far below roundoff and no subtractive cancellation.
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
            Combine(modes, shape);
        }

        /// <summary>
        /// Derivatives of the shape functions with respect to xi and eta at (xi, eta); both
        /// spans have length <see cref="Count"/>.
        /// </summary>
        public void EvaluateGradient(double xi, double eta, Span<double> dXi, Span<double> dEta)
        {
            const double h = 1e-30;
            Span<Complex> modes = stackalloc Complex[Count];
            Span<double> modeDerivative = stackalloc double[Count];

            EvaluateModes(Order, new Complex(xi, h), new Complex(eta, 0), modes);
            for (int j = 0; j < Count; j++) modeDerivative[j] = modes[j].Imaginary / h;
            Combine(modeDerivative, dXi);

            EvaluateModes(Order, new Complex(xi, 0), new Complex(eta, h), modes);
            for (int j = 0; j < Count; j++) modeDerivative[j] = modes[j].Imaginary / h;
            Combine(modeDerivative, dEta);
        }

        private void Combine(ReadOnlySpan<double> modes, Span<double> shape)
        {
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
        /// Proriol-Koornwinder-Dubiner modes psi_ij = P_i(a) * ((1 - b) / 2)^i * P_j^(2i+1,0)(b),
        /// i + j &lt;= p, on the collapsed coordinates (a, b) of the biunit triangle.
        /// <para>
        /// a = 2(1 + r)/(1 - s) - 1 is singular at the apex s = 1, so the first factor is
        /// computed as one polynomial, q_i = P_i(a) * c^i with c = (1 - b)/2, by the scaled
        /// Legendre recurrence (a * c = r + (1 + s)/2 needs no division). Evaluating a
        /// itself would lose the xi-dependence next to the apex.
        /// </para>
        /// </summary>
        private static void EvaluateModes<T>(int order, T xi, T eta, Span<T> modes)
            where T : unmanaged, INumberBase<T>
        {
            T one = T.One, half = T.CreateChecked(0.5), two = T.CreateChecked(2);
            T r = two * xi - one;
            T s = two * eta - one;
            T c = half * (one - s);
            T t = r + half * (one + s);

            Span<T> scaledLegendre = stackalloc T[order + 1];
            scaledLegendre[0] = one;
            if (order > 0) scaledLegendre[1] = t;
            for (int n = 1; n < order; n++)
                scaledLegendre[n + 1] = (T.CreateChecked(2 * n + 1) * t * scaledLegendre[n]
                    - T.CreateChecked(n) * c * c * scaledLegendre[n - 1]) / T.CreateChecked(n + 1);

            Span<T> jacobi = stackalloc T[order + 1];
            int m = 0;
            for (int i = 0; i <= order; i++)
            {
                Jacobi(order - i, 2 * i + 1, s, jacobi);
                for (int j = 0; i + j <= order; j++)
                    modes[m++] = scaledLegendre[i] * jacobi[j];
            }
        }

        /// <summary>Jacobi polynomials P_n^(alpha, 0)(x) for n = 0..maxDegree.</summary>
        private static void Jacobi<T>(int maxDegree, int alpha, T x, Span<T> values)
            where T : unmanaged, INumberBase<T>
        {
            values[0] = T.One;
            if (maxDegree == 0) return;
            values[1] = T.CreateChecked(0.5) * (T.CreateChecked(alpha + 2) * x + T.CreateChecked(alpha));
            for (int n = 1; n < maxDegree; n++)
            {
                double twoNAlpha = 2 * n + alpha;
                double denominator = 2.0 * (n + 1) * (n + alpha + 1) * twoNAlpha;
                T linear = T.CreateChecked(twoNAlpha + 1)
                    * (T.CreateChecked((twoNAlpha + 2) * twoNAlpha) * x + T.CreateChecked((double)alpha * alpha));
                T previous = T.CreateChecked(2.0 * (n + alpha) * n * (twoNAlpha + 2));
                values[n + 1] = (linear * values[n] - previous * values[n - 1]) / T.CreateChecked(denominator);
            }
        }
    }
}
