using System.Globalization;
using electrostat;
using Xunit;

namespace electrostat.Tests;

/// <summary>
/// Tests against a solved fixture: two axisymmetric windings with filleted top corners
/// (LV at r 130-160 mm, HV at r 220-250 mm, z 100-300 mm) and a pressboard barrier
/// between them, meshed with curved second-order elements and solved by Noumena at
/// order 2 for two scenarios ("HV impulse": HV 100 kV, LV 0; "LV impulse": HV 0,
/// LV 50 kV). The reference table holds the solver's own samples of V and E at the
/// Gmsh element nodes, taken from the same run: every element with a curved edge plus
/// every 8th other element.
/// </summary>
public class CurvedFixtureTests
{
    private static readonly string DataDir = Path.Combine(AppContext.BaseDirectory, "TestData");
    private static readonly Lazy<List<FieldSolution>> Solutions =
        new(() => Hdf5FieldResultsReader.Read(Path.Combine(DataDir, "two_windings_curved.h5")));

    private sealed record Sample(string Scenario, int Element, double X, double Y, double V, double Ex, double Ey);

    private static readonly Lazy<List<Sample>> Reference = new(() =>
        File.ReadLines(Path.Combine(DataDir, "two_windings_curved_reference.csv"))
            .Skip(1)
            .Select(line => line.Split(','))
            .Select(f => new Sample(f[0], int.Parse(f[1], CultureInfo.InvariantCulture),
                double.Parse(f[2], CultureInfo.InvariantCulture), double.Parse(f[3], CultureInfo.InvariantCulture),
                double.Parse(f[4], CultureInfo.InvariantCulture), double.Parse(f[5], CultureInfo.InvariantCulture),
                double.Parse(f[6], CultureInfo.InvariantCulture)))
            .ToList());

    private static FieldSolution Scenario(string name) => Solutions.Value.Single(s => s.ScenarioName == name);

    [Fact]
    public void ReadsBothScenariosWithCurvedGeometryAndFullOrderFields()
    {
        var solutions = Solutions.Value;
        Assert.Equal(["HV impulse", "LV impulse"], solutions.Select(s => s.ScenarioName));
        foreach (var sol in solutions)
        {
            Assert.NotNull(sol.Geometry);
            Assert.Equal(2, sol.Geometry!.Order);
            Assert.Equal(2, sol.Fields["V"].Order);
            Assert.Equal(2, sol.Fields["E"].Components);
            Assert.True(sol.TryGetPotential(out _));
            Assert.Contains("E", sol.ElementNodalFields.Keys);
        }
        // Scenarios share one mesh.
        Assert.Same(solutions[0].Mesh, solutions[1].Mesh);
    }

    [Theory]
    [InlineData("HV impulse", 100000.0, 0.0)]
    [InlineData("LV impulse", 0.0, 50000.0)]
    public void PotentialEqualsTheElectrodeVoltageOnEachWindingFace(string scenario, double hv, double lv)
    {
        var sol = Scenario(scenario);
        sol.TryGetPotential(out var potential);

        // Vertices on the straight inner faces of each winding (below the fillets).
        int checkedNodes = 0;
        foreach (var node in sol.Mesh!.Nodes)
        {
            if (node.Y < 100 || node.Y > 290) continue;
            double? expected = Math.Abs(node.X - 220) < 1e-9 ? hv : Math.Abs(node.X - 160) < 1e-9 ? lv : null;
            if (expected is null) continue;
            Assert.Equal(expected.Value, potential[(int)node.Id], 1e-6);
            checkedNodes++;
        }
        Assert.True(checkedNodes >= 4, $"only {checkedNodes} electrode vertices found");
    }

    [Fact]
    public void MatchesTheSolversOwnSamplesOfVAndGradV()
    {
        var samples = Reference.Value;
        Assert.True(samples.Count > 1000);
        double maxE = samples.Max(s => Math.Max(Math.Abs(s.Ex), Math.Abs(s.Ey)));

        Span<double> v = stackalloc double[1];
        Span<double> vXi = stackalloc double[1];
        Span<double> vEta = stackalloc double[1];
        Span<double> xXi = stackalloc double[2];
        Span<double> xEta = stackalloc double[2];
        foreach (var group in samples.GroupBy(s => s.Scenario))
        {
            var sol = Scenario(group.Key);
            var locator = new CurvedElementLocator(sol.Mesh!, sol.Geometry!);
            var potential = sol.Fields["V"];
            foreach (var s in group)
            {
                // Each sample is a node of its element; map it back to reference coordinates.
                Assert.True(locator.TryInvert(s.Element, s.X, s.Y, 1.0 / 3, 1.0 / 3, out double xi, out double eta));
                Assert.InRange(xi, -1e-8, 1 + 1e-8);
                Assert.InRange(eta, -1e-8, 1 - xi + 1e-8);

                potential.Evaluate(s.Element, xi, eta, v);
                Assert.Equal(s.V, v[0], 1e-6);

                // E = -grad V through the curved element's Jacobian.
                potential.EvaluateReferenceGradient(s.Element, xi, eta, vXi, vEta);
                sol.Geometry!.EvaluateReferenceGradient(s.Element, xi, eta, xXi, xEta);
                double det = xXi[0] * xEta[1] - xEta[0] * xXi[1];
                double ex = -(xEta[1] * vXi[0] - xXi[1] * vEta[0]) / det;
                double ey = -(-xEta[0] * vXi[0] + xXi[0] * vEta[0]) / det;
                Assert.True(Math.Abs(ex - s.Ex) <= 1e-9 * maxE, $"Ex {ex} vs {s.Ex} in element {s.Element}");
                Assert.True(Math.Abs(ey - s.Ey) <= 1e-9 * maxE, $"Ey {ey} vs {s.Ey} in element {s.Element}");
            }
        }
    }

    [Fact]
    public void SamplerFindsPointsInsideCurvedElementsAndReturnsTheirField()
    {
        var sol = Scenario("HV impulse");
        var sampler = new FemFieldSampler(sol);
        var geometry = sol.Geometry!;
        var potential = sol.Fields["V"];
        var rng = new Random(1234);
        Span<double> p = stackalloc double[2];
        Span<double> v = stackalloc double[1];

        int elements = sol.Mesh!.Elements.Count;
        for (int i = 0; i < 2000; i++)
        {
            int id = 1 + rng.Next(elements);
            // A point at least 0.02 inside each edge of the reference triangle.
            double a, b;
            do
            {
                a = 0.02 + 0.96 * rng.NextDouble();
                b = 0.02 + 0.96 * rng.NextDouble();
            } while (a + b > 0.98);
            geometry.Evaluate(id, a, b, p);
            potential.Evaluate(id, a, b, v);

            sampler.ResetHint();
            Assert.True(sampler.TryEvaluateV(p[0], p[1], out double sampled, out int found, out _),
                $"point ({p[0]}, {p[1]}) inside element {id} was not located");
            Assert.Equal(id, found);
            Assert.Equal(v[0], sampled, 1e-6);
        }
    }

    [Fact]
    public void SamplerReportsEInKilovoltsPerMillimetre()
    {
        var sample = Reference.Value.First(s => s.Scenario == "HV impulse");
        var sol = Scenario("HV impulse");
        var sampler = new FemFieldSampler(sol);

        // Nudge from the element node toward the element's centroid so the point is
        // unambiguously inside it (E is discontinuous between elements).
        Span<double> c = stackalloc double[2];
        sol.Geometry!.Evaluate(sample.Element, 1.0 / 3, 1.0 / 3, c);
        double x = sample.X + 1e-7 * (c[0] - sample.X), y = sample.Y + 1e-7 * (c[1] - sample.Y);

        Assert.True(sampler.TryEvaluateE(x, y, out double ex, out double ey, out int element, out _));
        Assert.Equal(sample.Element, element);
        Assert.Equal(sample.Ex * 1e-3, ex, 1e-6);
        Assert.Equal(sample.Ey * 1e-3, ey, 1e-6);
    }

    [Fact]
    public void SamplerRejectsPointsOutsideTheMesh()
    {
        var sampler = new FemFieldSampler(Scenario("HV impulse"));
        Assert.False(sampler.TryEvaluateV(-50, -50, out _, out _, out _));
        // Inside the HV winding, which is a hole in the mesh.
        Assert.False(sampler.TryEvaluateE(235, 200, out _, out _, out _, out _));
    }
}
