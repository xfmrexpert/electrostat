using electrostat;
using Xunit;

namespace electrostat.Tests;

public class LagrangeTriangleBasisTests
{
    /// <summary>Equispaced nodes of the order-p lattice on the reference triangle, flattened.</summary>
    private static double[] Lattice(int order)
    {
        var nodes = new List<double>();
        for (int j = 0; j <= order; j++)
            for (int i = 0; i + j <= order; i++)
                nodes.AddRange(order == 0 ? [1.0 / 3, 1.0 / 3] : [(double)i / order, (double)j / order]);
        return nodes.ToArray();
    }

    // A cubic and its gradient; any order-3 basis must reproduce both exactly.
    private static double Cubic(double x, double y) => x * x * y - 3 * x * y + 2 * y * y * y - x + 1;
    private static (double Dx, double Dy) CubicGradient(double x, double y) =>
        (2 * x * y - 3 * y - 1, x * x - 3 * x + 6 * y * y);

    private static readonly (double Xi, double Eta)[] SamplePoints =
    [
        (0.2, 0.3), (0.05, 0.9), (0.7, 0.1), (1.0 / 3, 1.0 / 3),
        (0, 0), (1, 0), (0, 1),          // vertices, including the apex eta = 1
        (1e-16, 1 - 1e-16), (0.5, 0.5),  // next to the apex; on the hypotenuse
    ];

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    [InlineData(6)]
    [InlineData(10)]
    public void ShapeFunctionsAreOneAtTheirNodeAndZeroAtTheOthers(int order)
    {
        var nodes = Lattice(order);
        var basis = new LagrangeTriangleBasis(nodes);
        Assert.Equal(order, basis.Order);

        var shape = new double[basis.Count];
        for (int i = 0; i < basis.Count; i++)
        {
            basis.Evaluate(nodes[2 * i], nodes[2 * i + 1], shape);
            for (int k = 0; k < basis.Count; k++)
                Assert.Equal(i == k ? 1.0 : 0.0, shape[k], 1e-9);
        }
    }

    [Fact]
    public void ReproducesACubicAndItsGradientExactly()
    {
        var nodes = Lattice(3);
        var basis = new LagrangeTriangleBasis(nodes);
        var shape = new double[basis.Count];
        var dXi = new double[basis.Count];
        var dEta = new double[basis.Count];

        foreach (var (xi, eta) in SamplePoints)
        {
            basis.Evaluate(xi, eta, shape);
            basis.EvaluateGradient(xi, eta, dXi, dEta);
            double value = 0, gx = 0, gy = 0;
            for (int k = 0; k < basis.Count; k++)
            {
                double f = Cubic(nodes[2 * k], nodes[2 * k + 1]);
                value += f * shape[k];
                gx += f * dXi[k];
                gy += f * dEta[k];
            }

            var (ex, ey) = CubicGradient(xi, eta);
            Assert.Equal(Cubic(xi, eta), value, 1e-10);
            Assert.Equal(ex, gx, 1e-9);
            Assert.Equal(ey, gy, 1e-9);
        }
    }

    [Fact]
    public void ShapeFunctionsSumToOneAndTheirGradientsToZero()
    {
        var basis = new LagrangeTriangleBasis(Lattice(4));
        var shape = new double[basis.Count];
        var dXi = new double[basis.Count];
        var dEta = new double[basis.Count];
        foreach (var (xi, eta) in SamplePoints)
        {
            basis.Evaluate(xi, eta, shape);
            basis.EvaluateGradient(xi, eta, dXi, dEta);
            Assert.Equal(1.0, shape.Sum(), 1e-10);
            Assert.Equal(0.0, dXi.Sum(), 1e-9);
            Assert.Equal(0.0, dEta.Sum(), 1e-9);
        }
    }

    [Fact]
    public void RejectsANodeCountThatIsNotACompleteSpace()
    {
        Assert.Throws<ArgumentException>(() => new LagrangeTriangleBasis([0, 0, 1, 0, 0, 1, 0.5, 0.5]));
    }
}
