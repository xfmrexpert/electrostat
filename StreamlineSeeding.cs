using System;
using System.Collections.Generic;
using GeometryLib;

namespace electrostat
{
    /// <summary>
    /// A single seed point for a streamline trace, expressed in mesh (r, z) coordinates.
    /// </summary>
    public readonly record struct StreamlineSeed(double X, double Y);

    /// <summary>
    /// Inputs available to a seed strategy: the solved field, the electrode/wall
    /// boundaries the streamlines terminate on, and the trace step scale.
    /// </summary>
    public sealed class StreamlineSeedContext
    {
        /// <summary>Field evaluator used to reject seeds outside the meshed domain.</summary>
        public required IFieldSampler Sampler { get; init; }

        /// <summary>
        /// Closed boundary loops of every electrode (and Dirichlet wall) in the model.
        /// All electrodes are treated uniformly: peak dielectric stress is a surface
        /// phenomenon, so seeds are launched from just off these curves.
        /// </summary>
        public required IReadOnlyList<GeomLineLoop> Boundaries { get; init; }

        /// <summary>Arc-length integration step (mesh units); also the seeding scale.</summary>
        public required double StepSize { get; init; }

        /// <summary>Seeds whose |E| is below this are discarded.</summary>
        public double MinFieldMagnitude { get; init; } = 1e-9;
    }

    /// <summary>
    /// Produces the set of seed points from which streamlines are traced. Implementations
    /// are pluggable so the seeding policy can evolve (boundary, curvature-weighted,
    /// adaptive, ...) without touching the trace/aggregation pipeline.
    /// </summary>
    public interface IStreamlineSeedStrategy
    {
        IEnumerable<StreamlineSeed> GenerateSeeds(StreamlineSeedContext context);
    }

    /// <summary>
    /// Seeds streamlines from points a short distance off every electrode boundary.
    /// <para>
    /// Rationale: the worst-case insulation stress occurs at conductor surfaces — and
    /// most acutely at high-curvature features (fillets, corners, ring edges) — not in
    /// the bulk oil. A uniform volume grid over-samples bulk oil and routinely misses
    /// these peaks. Walking the boundaries instead guarantees a flux line launches from
    /// each surface, and arc segments (fillets) are sampled more densely so the apex of
    /// a small radius is never skipped.
    /// </para>
    /// </summary>
    public sealed class ElectrodeBoundarySeedStrategy : IStreamlineSeedStrategy
    {
        /// <summary>
        /// Nominal arc-length spacing of seeds along straight boundary runs, expressed
        /// as a multiple of <see cref="StreamlineSeedContext.StepSize"/>.
        /// </summary>
        public double SpacingInSteps { get; set; } = 8.0;

        /// <summary>
        /// Extra sampling density applied to arcs (fillets/corners) relative to straight
        /// runs. A value of 4 samples curved segments four times as finely.
        /// </summary>
        public double CurvatureDensityFactor { get; set; } = 4.0;

        /// <summary>
        /// Distance the seed is pushed off the surface into the field, as a multiple of
        /// <see cref="StreamlineSeedContext.StepSize"/>. Large enough to land in the
        /// meshed dielectric, small enough to stay in the near-surface high-field region.
        /// </summary>
        public double OffsetInSteps { get; set; } = 1.5;

        public IEnumerable<StreamlineSeed> GenerateSeeds(StreamlineSeedContext context)
        {
            if (context is null) throw new ArgumentNullException(nameof(context));

            double step = context.StepSize;
            double spacing = Math.Max(SpacingInSteps * step, step);
            double offset = Math.Max(OffsetInSteps * step, step);
            double minE = context.MinFieldMagnitude;

            // Dedup seeds that fall on shared vertices between adjacent segments.
            var seen = new HashSet<(long, long)>();
            double quantum = Math.Max(0.5 * step, 1e-9);
            (long, long) Key(double x, double y) =>
                ((long)Math.Round(x / quantum), (long)Math.Round(y / quantum));

            foreach (var loop in context.Boundaries)
            {
                if (loop is null) continue;
                foreach (var entity in loop.Boundary)
                {
                    foreach (var (px, py, nx, ny) in SampleSegment(entity, spacing))
                    {
                        // Push off the surface along whichever normal direction lands in
                        // the meshed dielectric with a usable field magnitude.
                        if (TryOffsetSeed(context.Sampler, px, py, nx, ny, offset, minE, out var seed)
                            && seen.Add(Key(seed.X, seed.Y)))
                        {
                            yield return seed;
                        }
                    }
                }
            }
        }

        private bool TryOffsetSeed(
            IFieldSampler sampler,
            double px, double py, double nx, double ny,
            double offset, double minE, out StreamlineSeed seed)
        {
            double bestMag = -1;
            seed = default;
            for (int s = -1; s <= 1; s += 2)
            {
                double x = px + s * nx * offset;
                double y = py + s * ny * offset;
                if (!sampler.TryEvaluateE(x, y, out double ex, out double ey, out _, out _))
                    continue;
                double mag = Math.Sqrt(ex * ex + ey * ey);
                if (mag <= minE || mag <= bestMag) continue;
                bestMag = mag;
                seed = new StreamlineSeed(x, y);
            }
            return bestMag > 0;
        }

        /// <summary>
        /// Yield evenly spaced points along a boundary segment together with the unit
        /// normal at each point. Arcs are sampled <see cref="CurvatureDensityFactor"/>
        /// times more finely than straight lines.
        /// </summary>
        private IEnumerable<(double x, double y, double nx, double ny)> SampleSegment(
            GeomEntity entity, double spacing)
        {
            switch (entity)
            {
                case GeomLine line:
                {
                    double dx = line.pt2.x - line.pt1.x;
                    double dy = line.pt2.y - line.pt1.y;
                    double len = Math.Sqrt(dx * dx + dy * dy);
                    if (len < 1e-12) yield break;

                    double tx = dx / len, ty = dy / len;
                    double nx = -ty, ny = tx; // unit normal (either side handled by caller)

                    int n = Math.Max(1, (int)Math.Ceiling(len / spacing));
                    for (int i = 0; i <= n; i++)
                    {
                        double t = (double)i / n;
                        yield return (line.pt1.x + dx * t, line.pt1.y + dy * t, nx, ny);
                    }
                    break;
                }
                case GeomArc arc:
                {
                    var c = arc.Center;
                    double r = arc.Radius;
                    if (r < 1e-12) yield break;

                    double startAngle = Math.Atan2(arc.StartPt.y - c.y, arc.StartPt.x - c.x);
                    double sweep = arc.SweepAngle;
                    double len = Math.Abs(sweep) * r;

                    double arcSpacing = Math.Max(spacing / Math.Max(CurvatureDensityFactor, 1.0), 1e-12);
                    int n = Math.Max(2, (int)Math.Ceiling(len / arcSpacing));
                    for (int i = 0; i <= n; i++)
                    {
                        double a = startAngle + sweep * ((double)i / n);
                        double ca = Math.Cos(a), sa = Math.Sin(a);
                        // Radial direction is the surface normal for a circular arc.
                        yield return (c.x + r * ca, c.y + r * sa, ca, sa);
                    }
                    break;
                }
            }
        }
    }
}
