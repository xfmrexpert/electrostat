using System;
using System.Collections.Generic;
using System.Linq;
using GeometryLib;
using TfmrLib.FEM;

string path = args.Length > 0
    ? args[0]
    : @"C:\Users\tcraymond\source\repos\electrostat\bin\Debug\net10.0\Window_Cut__no_adjacent_phase_\geom.results.msh";

Console.WriteLine($"Loading: {path}");
var sol = FEMSolution.Load(path);
Console.WriteLine($"Mesh nodes: {sol.Mesh!.Nodes.Count}, elements: {sol.Mesh.Elements.Count}");
Console.WriteLine($"Nodal scalar views: [{string.Join(", ", sol.NodalScalars.Keys)}]");
Console.WriteLine($"Element-nodal views: [{string.Join(", ", sol.ElementNodalFields.Keys)}]");

if (!sol.TryGetPotential(out var potential))
{
    Console.WriteLine("FAIL: no potential field in results.");
    return 1;
}
Console.WriteLine($"Potential samples: {potential.Count}");

var sampler = new FemFieldSampler(sol);
var bounds = sampler.Locator.Bounds;
Console.WriteLine($"Mesh bounds: [{bounds.MinX:G6}, {bounds.MinY:G6}] x [{bounds.MaxX:G6}, {bounds.MaxY:G6}]");
Console.WriteLine($"Triangles indexed: {sampler.Locator.TriangleCount}");

// --- Test 1: V interpolation reproduces nodal values ---
var rng = new Random(42);
var nodesWithV = sol.Mesh.Nodes.Where(n => potential.ContainsKey((int)n.Id)).ToArray();
int nTest = Math.Min(50, nodesWithV.Length);
double maxErrV = 0;
int hits = 0;
for (int i = 0; i < nTest; i++)
{
    var n = nodesWithV[rng.Next(nodesWithV.Length)];
    sampler.ResetHint();
    if (sampler.TryEvaluateV(n.X, n.Y, out double v, out int eid, out int mat))
    {
        hits++;
        double truth = potential[(int)n.Id];
        double err = Math.Abs(v - truth);
        if (err > maxErrV) maxErrV = err;
    }
}
Console.WriteLine($"V at nodes: {hits}/{nTest} located, max |err| = {maxErrV:E3}");

// --- Test 2: walk-locate hint speeds successive nearby queries ---
// Just make sure repeated calls along a line don't crash and stay in-bounds.
sampler.ResetHint();
double midX = 0.5 * (bounds.MinX + bounds.MaxX);
double y0 = bounds.MinY + 0.05 * (bounds.MaxY - bounds.MinY);
double y1 = bounds.MaxY - 0.05 * (bounds.MaxY - bounds.MinY);
int locatedAlongLine = 0;
const int N = 200;
for (int i = 0; i < N; i++)
{
    double t = i / (double)(N - 1);
    double y = y0 + (y1 - y0) * t;
    if (sampler.TryEvaluateV(midX, y, out _, out _, out _))
        locatedAlongLine++;
}
Console.WriteLine($"Vertical scan at x={midX:G4}: {locatedAlongLine}/{N} samples inside mesh.");

// --- Test 3: E interpolation produces finite values ---
sampler.ResetHint();
int eHits = 0; double maxAbsE = 0;
for (int i = 0; i < 100; i++)
{
    double rx = bounds.MinX + (bounds.MaxX - bounds.MinX) * rng.NextDouble();
    double ry = bounds.MinY + (bounds.MaxY - bounds.MinY) * rng.NextDouble();
    if (sampler.TryEvaluateE(rx, ry, out double ex, out double ey, out _, out _))
    {
        eHits++;
        double mag = Math.Sqrt(ex * ex + ey * ey);
        if (mag > maxAbsE) maxAbsE = mag;
        if (!double.IsFinite(mag))
        {
            Console.WriteLine($"FAIL: non-finite E at ({rx},{ry}).");
            return 1;
        }
    }
}
Console.WriteLine($"E samples: {eHits}/100 located, max |E| = {maxAbsE:E3}");

if (maxErrV > 1e-6)
{
    Console.WriteLine($"FAIL: V interpolation error too large ({maxErrV:E3}).");
    return 1;
}

// --- Test 4: streamline tracer from a few interior seeds ---
{
    sampler.ResetHint();
    var tracer = new StreamlineTracer(sampler, new StreamlineTracerOptions
    {
        StepSize = 0.5,
        MaxSteps = 20_000,
        Direction = TraceDirection.Backward, // follow -E
    });

    // Seed at a few random in-mesh points for a sanity check.
    var seeds = new List<(double X, double Y)>();
    int tries = 0;
    while (seeds.Count < 5 && tries++ < 500)
    {
        double rx = bounds.MinX + (bounds.MaxX - bounds.MinX) * rng.NextDouble();
        double ry = bounds.MinY + (bounds.MaxY - bounds.MinY) * rng.NextDouble();
        if (sampler.TryEvaluateE(rx, ry, out double ex, out double ey, out _, out _) &&
            Math.Sqrt(ex * ex + ey * ey) > 1e-6)
        {
            seeds.Add((rx, ry));
        }
    }

    Console.WriteLine($"Tracing {seeds.Count} streamlines:");
    foreach (var (sx, sy) in seeds)
    {
        var line = tracer.Trace(sx, sy);
        Console.WriteLine(
            $"  seed=({sx:G5},{sy:G5})  pts={line.Points.Count,5}  L={line.TotalLength:G5}  end={line.TerminationReason}");
        if (line.Points.Count > 0)
        {
            var last = line.Points[^1];
            if (!double.IsFinite(last.X) || !double.IsFinite(last.Y))
            {
                Console.WriteLine("FAIL: non-finite endpoint.");
                return 1;
            }
        }
    }

    // Monotonic-V check: along a backward (along -E = +∇V) trace, V should strictly increase.
    sampler.ResetHint();
    var sample = seeds[0];
    var traced = tracer.Trace(sample.X, sample.Y);
    if (traced.Points.Count >= 3 && _potentialAvailable(sampler))
    {
        double prevV = double.NaN;
        int violations = 0;
        int checkedPts = 0;
        foreach (var p in traced.Points)
        {
            if (sampler.TryEvaluateV(p.X, p.Y, out double v, out _, out _))
            {
                checkedPts++;
                // Allow tiny numerical wobble at material interfaces.
                if (!double.IsNaN(prevV) && v < prevV - 1e-6) violations++;
                prevV = v;
            }
        }
        Console.WriteLine($"  Monotonic-V (increasing) check: {violations} violations / {checkedPts} pts");
        if (violations > checkedPts / 20)
        {
            Console.WriteLine("FAIL: V is not monotonically increasing along -E trace.");
            return 1;
        }

        // --- Test 4b: per-material segmentation + ∫|E|·dl ≈ ΔV check ---
        var stress = StreamlineStressCalculator.Compute(traced);
        Console.WriteLine($"  Stress: total L={stress.TotalLength:G5}, ∫|E|dl={stress.TotalIntegralEdL:G5}, " +
                          $"max|E|={stress.MaxE:G5}, segments={stress.Segments.Count}");
        foreach (var seg in stress.Segments)
        {
            Console.WriteLine($"    mat={seg.MaterialTag,4}  L={seg.Length,8:G4}  ∫Edl={seg.IntegralEdL,10:G4}  " +
                              $"mean|E|={seg.MeanE,9:G4}  max|E|={seg.MaxE,9:G4}");
        }

        // ∫|E|·dl along -E should equal V_end - V_start (= ΔV). |E| is in kV/mm so the
        // integral is in kV; convert ΔV (volts) to kV before comparing.
        if (sampler.TryEvaluateV(traced.Points[0].X, traced.Points[0].Y, out double v0, out _, out _) &&
            sampler.TryEvaluateV(traced.Points[^1].X, traced.Points[^1].Y, out double vN, out _, out _))
        {
            double dvKv = (vN - v0) / 1000.0;
            double rel = Math.Abs(stress.TotalIntegralEdL - dvKv) / Math.Max(Math.Abs(dvKv), 1e-12);
            Console.WriteLine($"  ∫|E|dl vs ΔV: integral={stress.TotalIntegralEdL:G5} kV, ΔV={dvKv:G5} kV, rel-err={rel:E3}");
            if (rel > 0.05)
            {
                Console.WriteLine("FAIL: ∫|E|·dl does not match ΔV within 5%.");
                return 1;
            }
        }
    }
}

// --- Test 5: geometry clipping with a synthetic rectangular loop ---
{
    // Pick a known-meshed seed/path from earlier traces, then build a small rectangle
    // a few mm downstream so the streamline is guaranteed to cross it.
    sampler.ResetHint();
    var baseTracer = new StreamlineTracer(sampler, new StreamlineTracerOptions
    {
        StepSize = 0.5,
        MaxSteps = 2_000,
        Direction = TraceDirection.Backward,
    });

    StreamlinePoint? rectPoint = null;
    StreamlinePoint? seedPoint = null;
    for (int i = 0; i < 20 && rectPoint == null; i++)
    {
        double rx = bounds.MinX + (bounds.MaxX - bounds.MinX) * rng.NextDouble();
        double ry = bounds.MinY + (bounds.MaxY - bounds.MinY) * rng.NextDouble();
        if (!sampler.TryEvaluateE(rx, ry, out double ex, out double ey, out _, out _)) continue;
        if (!(Math.Sqrt(ex * ex + ey * ey) > 1e-6)) continue;
        var line = baseTracer.Trace(rx, ry);
        if (line.Points.Count > 30)
        {
            seedPoint = line.Points[0];
            rectPoint = line.Points[line.Points.Count / 2]; // mid-trace
        }
    }
    if (seedPoint == null || rectPoint == null)
    { Console.WriteLine("WARN: no usable trace for clipping test, skipping."); }
    else
    {
        double rcx = rectPoint.Value.X;
        double rcy = rectPoint.Value.Y;
        double half = 0.5; // small box centred on a point we know is mid-trace
        var p1 = new GeomPoint(rcx - half, rcy - half);
        var p2 = new GeomPoint(rcx + half, rcy - half);
        var p3 = new GeomPoint(rcx + half, rcy + half);
        var p4 = new GeomPoint(rcx - half, rcy + half);
        var loop = new GeomLineLoop(new List<GeomEntity>
        {
            new GeomLine(p1, p2),
            new GeomLine(p2, p3),
            new GeomLine(p3, p4),
            new GeomLine(p4, p1),
        });
        var clipper = new GeometryClipper(new[] { new ClipLoop(42, loop, IsHole: false) });
        var clippedTracer = new StreamlineTracer(
            sampler,
            new StreamlineTracerOptions { StepSize = 0.5, MaxSteps = 20_000, Direction = TraceDirection.Backward },
            clipper);

        sampler.ResetHint();
        var clipped = clippedTracer.Trace(seedPoint.Value.X, seedPoint.Value.Y);
        Console.WriteLine($"Clipping test: seed=({seedPoint.Value.X:G5},{seedPoint.Value.Y:G5}), " +
                          $"rect@({rcx:G5},{rcy:G5}) ±{half}, " +
                          $"end={clipped.TerminationReason}, surface={clipped.HitSurfaceId}, " +
                          $"L={clipped.TotalLength:G5}");

        if (clipped.TerminationReason != TraceTerminationReason.HitConductor || clipped.HitSurfaceId != 42)
        {
            Console.WriteLine("FAIL: clipped trace did not terminate on synthetic rect.");
            return 1;
        }
        var endpoint = clipped.Points[^1];
        bool onWall =
            Math.Abs(endpoint.X - (rcx - half)) < 1e-6 || Math.Abs(endpoint.X - (rcx + half)) < 1e-6 ||
            Math.Abs(endpoint.Y - (rcy - half)) < 1e-6 || Math.Abs(endpoint.Y - (rcy + half)) < 1e-6;
        if (!onWall)
        {
            Console.WriteLine($"FAIL: clip endpoint ({endpoint.X:G6},{endpoint.Y:G6}) not on a rect wall.");
            return 1;
        }
        // No interior points except possibly the final one (which is on a wall, allowed).
        for (int i = 0; i < clipped.Points.Count - 1; i++)
        {
            var p = clipped.Points[i];
            const double pad = 1e-6;
            if (p.X > rcx - half + pad && p.X < rcx + half - pad &&
                p.Y > rcy - half + pad && p.Y < rcy + half - pad)
            {
                Console.WriteLine($"FAIL: pre-termination point penetrated rect interior at ({p.X},{p.Y}).");
                return 1;
            }
        }
    }
}

Console.WriteLine("PASS");
return 0;

static bool _potentialAvailable(FemFieldSampler s) => s != null;
