using System;
using System.Collections.Generic;

namespace electrostat
{
    /// <summary>
    /// Direction along which a streamline is traced relative to the field vector E.
    /// </summary>
    public enum TraceDirection
    {
        /// <summary>Trace along +E (from low to high potential when field is E = -∇V).</summary>
        Forward,
        /// <summary>Trace along -E (from high to low potential, i.e. high-V conductor → ground).</summary>
        Backward,
    }

    /// <summary>
    /// Reason a streamline trace terminated.
    /// </summary>
    public enum TraceTerminationReason
    {
        /// <summary>The integrator left the meshed domain.</summary>
        ExitedDomain,
        /// <summary>|E| fell below <see cref="StreamlineTracerOptions.MinFieldMagnitude"/>.</summary>
        FieldTooWeak,
        /// <summary>The maximum step count was reached.</summary>
        MaxStepsExceeded,
        /// <summary>The maximum arc length was reached.</summary>
        MaxLengthExceeded,
        /// <summary>The seed point was outside the meshed domain.</summary>
        SeedOutsideDomain,
        /// <summary>The streamline crossed a registered geometry boundary (conductor / barrier).</summary>
        HitConductor,
    }

    /// <summary>Options for <see cref="StreamlineTracer"/>.</summary>
    public sealed class StreamlineTracerOptions
    {
        /// <summary>Arc-length step size (mesh-coordinate units, typically mm).</summary>
        public double StepSize { get; set; } = 0.05;

        /// <summary>Hard cap on integration steps.</summary>
        public int MaxSteps { get; set; } = 20_000;

        /// <summary>Hard cap on accumulated arc length.</summary>
        public double MaxLength { get; set; } = double.PositiveInfinity;

        /// <summary>Stop if |E| drops below this threshold (kV/mm in typical use).</summary>
        public double MinFieldMagnitude { get; set; } = 0.0;

        /// <summary>Direction of integration relative to E.</summary>
        public TraceDirection Direction { get; set; } = TraceDirection.Backward;

        /// <summary>
        /// When a step crosses from one material (physical tag) into another, bisect the
        /// step to locate the interface and record a sample on each side of it. This snaps
        /// the recorded material transition onto the true element/material boundary instead
        /// of leaving it up to half a step away, and captures the field's discontinuity at
        /// dielectric interfaces. Set to 0 to disable.
        /// </summary>
        public int MaterialInterfaceBisections { get; set; } = 20;
    }

    /// <summary>
    /// One sampled point along a streamline. Field components (<paramref name="Ex"/>,
    /// <paramref name="Ey"/>) and <paramref name="EMagnitude"/> are in kV/mm.
    /// </summary>
    public readonly record struct StreamlinePoint(
        double X, double Y,
        double Ex, double Ey,
        double EMagnitude,
        int ElementId,
        int MaterialTag,
        double ArcLength);

    /// <summary>
    /// Result of a single streamline trace.
    /// </summary>
    public sealed class Streamline
    {
        public required IReadOnlyList<StreamlinePoint> Points { get; init; }
        public required double TotalLength { get; init; }
        public required TraceTerminationReason TerminationReason { get; init; }
        /// <summary>Surface id hit at termination (only set when <see cref="TerminationReason"/> is <see cref="TraceTerminationReason.HitConductor"/>).</summary>
        public int? HitSurfaceId { get; init; }
    }

    /// <summary>
    /// Fixed-step RK4 streamline integrator over an <see cref="IFieldSampler"/>.
    /// Steps in arc length along the unit field direction so the spatial step is
    /// uniform regardless of |E|. Termination conditions in this stage are limited
    /// to leaving the meshed domain, hitting a field floor, and the step / length
    /// caps. Geometry-based termination (conductor intersection) lands in Step 3.
    /// </summary>
    public sealed class StreamlineTracer
    {
        private readonly IFieldSampler _sampler;
        private readonly StreamlineTracerOptions _options;
        private readonly GeometryClipper? _clipper;

        public StreamlineTracer(IFieldSampler sampler,
            StreamlineTracerOptions? options = null,
            GeometryClipper? clipper = null)
        {
            _sampler = sampler ?? throw new ArgumentNullException(nameof(sampler));
            _options = options ?? new StreamlineTracerOptions();
            _clipper = clipper;
            if (_options.StepSize <= 0)
                throw new ArgumentException("StepSize must be positive.", nameof(options));
        }

        /// <summary>
        /// Trace a single streamline starting from <paramref name="seedX"/>,<paramref name="seedY"/>.
        /// </summary>
        public Streamline Trace(double seedX, double seedY)
        {
            var pts = new List<StreamlinePoint>(256);

            double sign = _options.Direction == TraceDirection.Forward ? +1.0 : -1.0;
            double h = _options.StepSize;

            // Seed sample.
            if (!TrySampleUnit(seedX, seedY, sign, out var seedSample))
            {
                return new Streamline
                {
                    Points = pts,
                    TotalLength = 0,
                    TerminationReason = TraceTerminationReason.SeedOutsideDomain,
                };
            }
            pts.Add(seedSample.Point with { ArcLength = 0 });

            double x = seedX, y = seedY;
            double s = 0;
            var reason = TraceTerminationReason.MaxStepsExceeded;
            int? hitSurfaceId = null;

            for (int step = 0; step < _options.MaxSteps; step++)
            {
                // RK4 in arc-length: dx/ds = u(x), where u = sign * E / |E|.
                // If any sub-evaluation falls outside the mesh, we may still be able to
                // terminate cleanly via the geometry clipper using k1 (which we have if
                // the current point is in the mesh). That handles the common case where
                // the next step naturally crosses a conductor boundary.
                if (!TryUnitDir(x, y, sign, out double k1x, out double k1y))
                { reason = TraceTerminationReason.ExitedDomain; break; }

                double dx, dy;

                if (TryUnitDir(x + 0.5 * h * k1x, y + 0.5 * h * k1y, sign, out double k2x, out double k2y) &&
                    TryUnitDir(x + 0.5 * h * k2x, y + 0.5 * h * k2y, sign, out double k3x, out double k3y) &&
                    TryUnitDir(x + h * k3x,       y + h * k3y,       sign, out double k4x, out double k4y))
                {
                    dx = (h / 6.0) * (k1x + 2 * k2x + 2 * k3x + k4x);
                    dy = (h / 6.0) * (k1y + 2 * k2y + 2 * k3y + k4y);
                }
                else
                {
                    // Fall back to Euler with k1 so the clipper still gets a chance to
                    // catch the boundary crossing on this step.
                    dx = h * k1x;
                    dy = h * k1y;
                }

                double nx = x + dx;
                double ny = y + dy;

                // Geometry clip: stop on the first conductor crossed by this step.
                if (_clipper != null && _clipper.TryClipSegment(x, y, nx, ny, out var clip))
                {
                    double cdx = clip.X - x;
                    double cdy = clip.Y - y;
                    s += Math.Sqrt(cdx * cdx + cdy * cdy);
                    if (TrySampleUnit(clip.X, clip.Y, sign, out var clipSample))
                        pts.Add(clipSample.Point with { ArcLength = s });
                    else
                        pts.Add(new StreamlinePoint(clip.X, clip.Y, 0, 0, 0, 0, 0, s));

                    hitSurfaceId = clip.SurfaceId;
                    reason = TraceTerminationReason.HitConductor;
                    break;
                }

                double pxPrev = x, pyPrev = y, sPrev = s;
                x = nx;
                y = ny;
                s += Math.Sqrt(dx * dx + dy * dy);

                // Sample at the new location for the recorded point and field-floor test.
                if (!TrySampleUnit(x, y, sign, out var sample))
                { reason = TraceTerminationReason.ExitedDomain; break; }

                // If this step crossed a material boundary, bisect it to pin the transition
                // onto the true interface and record the (discontinuous) field on each side,
                // instead of leaving the transition up to half a step away.
                int prevTag = pts[pts.Count - 1].MaterialTag;
                if (_options.MaterialInterfaceBisections > 0 &&
                    sample.Point.MaterialTag != prevTag)
                {
                    InsertMaterialInterface(pts, pxPrev, pyPrev, prevTag, sPrev, x, y, sign);
                }

                if (sample.Magnitude < _options.MinFieldMagnitude)
                {
                    pts.Add(sample.Point with { ArcLength = s });
                    reason = TraceTerminationReason.FieldTooWeak;
                    break;
                }

                pts.Add(sample.Point with { ArcLength = s });

                if (s >= _options.MaxLength)
                {
                    reason = TraceTerminationReason.MaxLengthExceeded;
                    break;
                }
            }

            return new Streamline
            {
                Points = pts,
                TotalLength = s,
                TerminationReason = reason,
                HitSurfaceId = hitSurfaceId,
            };
        }

        /// <summary>
        /// Bisects the straight segment (ax,ay)→(bx,by), which crosses from material
        /// <paramref name="tagA"/> into a different material, to locate the interface, then
        /// appends two samples straddling it: the last point still in <paramref name="tagA"/>
        /// and the first point on the far side. Both are placed at the interface arc length
        /// so the transition is recorded exactly on the boundary and the field's
        /// discontinuity there is preserved. The caller still appends the segment's endpoint
        /// afterwards.
        /// </summary>
        private void InsertMaterialInterface(
            List<StreamlinePoint> pts,
            double ax, double ay, int tagA, double sA,
            double bx, double by, double sign)
        {
            // Binary search for the parameter t in (0,1] where the tag first changes from
            // tagA. lo stays in tagA; hi is on the far side.
            double lo = 0.0, hi = 1.0;
            double loX = ax, loY = ay;
            double hiX = bx, hiY = by;
            for (int i = 0; i < _options.MaterialInterfaceBisections; i++)
            {
                double mid = 0.5 * (lo + hi);
                double mx = ax + mid * (bx - ax);
                double my = ay + mid * (by - ay);
                if (_sampler.TryEvaluateE(mx, my, out _, out _, out _, out int mtag) && mtag == tagA)
                {
                    lo = mid; loX = mx; loY = my;
                }
                else
                {
                    hi = mid; hiX = mx; hiY = my;
                }
            }

            // Arc length at the interface (measured along the straight step from a).
            double segLen = Math.Sqrt((bx - ax) * (bx - ax) + (by - ay) * (by - ay));
            double sInterface = sA + segLen * hi;

            // Near side: last sample still inside tagA.
            if (TrySampleUnit(loX, loY, sign, out var nearSample) &&
                nearSample.Point.MaterialTag == tagA)
            {
                pts.Add(nearSample.Point with { X = loX, Y = loY, ArcLength = sInterface });
            }

            // Far side: first sample across the interface. Recorded at the same arc length
            // so the pair marks a zero-length jump exactly on the boundary.
            if (TrySampleUnit(hiX, hiY, sign, out var farSample))
            {
                pts.Add(farSample.Point with { X = hiX, Y = hiY, ArcLength = sInterface });
            }
        }

        /// <summary>
        /// Convenience: trace many seeds.
        /// </summary>
        public IReadOnlyList<Streamline> TraceMany(IEnumerable<(double X, double Y)> seeds)
        {
            var list = new List<Streamline>();
            foreach (var (sx, sy) in seeds) list.Add(Trace(sx, sy));
            return list;
        }

        private bool TryUnitDir(double x, double y, double sign, out double ux, out double uy)
        {
            if (!_sampler.TryEvaluateE(x, y, out double ex, out double ey, out _, out _))
            {
                ux = uy = 0;
                return false;
            }
            double mag = Math.Sqrt(ex * ex + ey * ey);
            if (mag <= 0 || !double.IsFinite(mag))
            {
                ux = uy = 0;
                return false;
            }
            double inv = sign / mag;
            ux = ex * inv;
            uy = ey * inv;
            return true;
        }

        private bool TrySampleUnit(double x, double y, double sign, out (StreamlinePoint Point, double Magnitude) sample)
        {
            if (!_sampler.TryEvaluateE(x, y, out double ex, out double ey, out int eid, out int mat))
            {
                sample = default;
                return false;
            }
            double mag = Math.Sqrt(ex * ex + ey * ey);
            sample = (new StreamlinePoint(x, y, ex, ey, mag, eid, mat, 0), mag);
            return true;
        }
    }
}
