// End-to-end check of the build -> mesh -> solve -> read pipeline, as the UI runs it.
//
// Builds a two-winding axisymmetric case (filleted top corners, a pressboard barrier
// between the windings, two voltage scenarios) through GeometryBuilder.BuildMesh with
// its default (curved, second-order) mesh, solves it with Noumena, reads the results
// back and checks them. Needs gmsh (GMSH_PATH or on PATH) and Noumena (NOUMENA_EXE or
// 'noumena' on PATH). Exits 0 when every check passes, 1 otherwise, and prints the
// results archive path last so later steps can reuse it.
//
// usage: EndToEndCheck [output directory]   (default: ./e2e)

using electrostat;
using TfmrLib.FEM;

string outputDir = Path.GetFullPath(args.Length > 0 ? args[0] : "e2e");
int failures = 0;

void Check(bool condition, string description)
{
    Console.WriteLine($"{(condition ? "PASS" : "FAIL")}  {description}");
    if (!condition) failures++;
}

const double HvVoltage = 100_000;
const double LvVoltage = 50_000;
var domain = new Domain(RInner: 100, ROuter: 300, ZLower: 0, ZUpper: 400);
var electrostatCase = new ElectrostatCase("e2e", domain,
    new List<WindingBlock> { new("LV", 130, 100, 30, 200, 3), new("HV", 220, 100, 30, 200, 3) },
    new List<PressboardBarrier> { new("PB1", 185, 80, 3, 240) },
    new List<AngleRing>(), new List<StaticRing>(),
    new Dictionary<string, double> { ["LV"] = 0, ["HV"] = HvVoltage, ["Core"] = 0, ["Tank"] = 0, ["TopYoke"] = 0 },
    new List<VoltageScenario>
    {
        new("HV impulse", new Dictionary<string, double> { ["HV"] = HvVoltage }),
        new("LV impulse", new Dictionary<string, double> { ["HV"] = 0, ["LV"] = LvVoltage }),
    });

Console.WriteLine($"Building and meshing in {outputDir}");
var model = GeometryBuilder.BuildMesh(
    electrostatCase with { Voltages = electrostatCase.EffectiveVoltages(null) }, outputDir, lc: 8.0);

Console.WriteLine("Solving");
model.Solve(new Progress<NoumenaProgressEvent>(e =>
{
    if (e.Level is "error" or "warning") Console.WriteLine($"  [{e.Level}] {e.Message}");
}));

Check(model.LoadError == null, $"results loaded{(model.LoadError == null ? "" : ": " + model.LoadError)}");
Check(model.ScenarioSolutions.Count == 2 && model.ScenarioSolutions.All(s => s.Solution != null),
    "both scenarios solved and loaded");
if (failures > 0) return 1;

var expected = new Dictionary<string, (double Hv, double Lv)>
{
    ["HV impulse"] = (HvVoltage, 0),
    ["LV impulse"] = (0, LvVoltage),
};

foreach (var (name, solution) in model.ScenarioSolutions.Select(s => (s.Name, s.Solution!)))
{
    var (hv, lv) = expected[name];
    Console.WriteLine($"Scenario '{name}': {solution.Mesh!.Elements.Count} elements");

    // The default mesh is curved, and the solver exports its geometry and FE spaces.
    Check(solution.Geometry is { Order: 2 }, $"{name}: curved order-2 geometry");
    Check(solution.Fields.TryGetValue("V", out var v) && v.Order == 2, $"{name}: V at full order 2");

    // Dirichlet values on the straight inner faces of the windings (below the fillets).
    solution.TryGetPotential(out var potential);
    double worst = 0;
    int faceNodes = 0;
    foreach (var node in solution.Mesh.Nodes)
    {
        if (node.Y < 100 || node.Y > 290) continue;
        double? target = Math.Abs(node.X - 220) < 1e-9 ? hv : Math.Abs(node.X - 160) < 1e-9 ? lv : null;
        if (target is null) continue;
        worst = Math.Max(worst, Math.Abs(potential[(int)node.Id] - target.Value));
        faceNodes++;
    }
    Check(faceNodes > 10 && worst < 1e-6, $"{name}: V on winding faces = applied voltage ({faceNodes} nodes, max error {worst:E1} V)");

    double vMin = potential.Values.Min(), vMax = potential.Values.Max();
    Check(vMin > -1e-6 && vMax < Math.Max(hv, lv) + 1e-6, $"{name}: V within [0, {Math.Max(hv, lv)}] (got [{vMin:F1}, {vMax:F1}])");

    // Between the windings the field points from the energized winding to the grounded one.
    var sampler = new FemFieldSampler(solution);
    bool located = sampler.TryEvaluateE(175, 200, out double er, out double ez, out _, out _);
    double expectedSign = hv > lv ? -1 : 1;
    Check(located && Math.Sign(er) == expectedSign && Math.Abs(ez) < 0.05 * Math.Abs(er),
        $"{name}: E between the windings is radial, toward the grounded winding (Er = {er:F3}, Ez = {ez:F4} kV/mm)");
    Check(Math.Abs(er) > 0.3 && Math.Abs(er) < 5, $"{name}: |E| between the windings is plausible ({Math.Abs(er):F3} kV/mm)");
}

string resultsFile = Path.Combine(outputDir, "results.h5");
Console.WriteLine(failures == 0 ? "All checks passed." : $"{failures} check(s) failed.");
Console.WriteLine(resultsFile);
return failures == 0 ? 0 : 1;
