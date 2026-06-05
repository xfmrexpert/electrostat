using System;
using System.Collections.Generic;
using System.Globalization;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using electrostat;

namespace electrostat_UI.Views
{
    /// <summary>
    /// A lightweight, self-drawn plot of stress and margin along a single
    /// <see cref="StreamlineWithMargin"/>. Two stacked panels share an arc-length
    /// X axis:
    /// <list type="bullet">
    /// <item>top: local |E| (solid) versus the Wiedmann allowable field (dashed), kV/mm;</item>
    /// <item>bottom: cumulative safety margin E_design / E_average with a red limit line at 1.0.</item>
    /// </list>
    /// Rendered with Avalonia's vector <see cref="DrawingContext"/> (no GPU lease
    /// needed) since the data is at most a few thousand points and the window is
    /// static once opened.
    /// </summary>
    public class StreamlineStressPlotView : Control
    {
        public static readonly StyledProperty<StreamlineWithMargin?> StreamlineProperty =
            AvaloniaProperty.Register<StreamlineStressPlotView, StreamlineWithMargin?>(nameof(Streamline));

        public static readonly StyledProperty<int> StreamlineNumberProperty =
            AvaloniaProperty.Register<StreamlineStressPlotView, int>(nameof(StreamlineNumber));

        public StreamlineWithMargin? Streamline
        {
            get => GetValue(StreamlineProperty);
            set => SetValue(StreamlineProperty, value);
        }

        public int StreamlineNumber
        {
            get => GetValue(StreamlineNumberProperty);
            set => SetValue(StreamlineNumberProperty, value);
        }

        private static readonly IBrush s_axisBrush = new SolidColorBrush(Color.FromRgb(60, 60, 60));
        private static readonly IBrush s_gridBrush = new SolidColorBrush(Color.FromArgb(40, 0, 0, 0));
        private static readonly IBrush s_stressBrush = new SolidColorBrush(Color.FromRgb(30, 90, 200));
        private static readonly IBrush s_avgBrush = new SolidColorBrush(Color.FromRgb(210, 120, 0));
        private static readonly IBrush s_designBrush = new SolidColorBrush(Color.FromRgb(20, 150, 60));
        private static readonly IBrush s_fallingBrush = new SolidColorBrush(Color.FromArgb(150, 110, 110, 110));
        private static readonly IBrush s_sepBrush = new SolidColorBrush(Color.FromArgb(50, 0, 0, 0));
        private static readonly IBrush s_limitBrush = new SolidColorBrush(Color.FromRgb(210, 40, 40));

        static StreamlineStressPlotView()
        {
            AffectsRender<StreamlineStressPlotView>(StreamlineProperty, StreamlineNumberProperty);
        }

        public StreamlineStressPlotView()
        {
            ClipToBounds = true;
        }

        public override void Render(DrawingContext context)
        {
            base.Render(context);
            context.FillRectangle(Brushes.White, new Rect(Bounds.Size));

            var s = Streamline;
            var pts = s?.Points;
            if (s == null || pts == null || pts.Count < 2)
            {
                DrawCenteredText(context, "No streamline data.", s_axisBrush);
                return;
            }

            // Title.
            string title = $"Streamline #{StreamlineNumber} — cumulative-method stress & margin";
            DrawText(context, title, new Point(12, 6), 15, s_axisBrush, bold: true);

            var gaps = s.OilGaps;
            if (gaps == null || gaps.Count == 0)
            {
                DrawCenteredText(context,
                    "This streamline has no oil gap — the Weidmann cumulative method does not apply.",
                    s_axisBrush);
                return;
            }

            // Lay the oil gaps out end-to-end on a single cumulative-distance axis. Each
            // gap's curves run over d ∈ (0, gapLength]; a small pad separates gaps.
            const double gapPad = 2.0; // mm of whitespace between gaps on the axis
            var gapX0 = new double[gaps.Count]; // axis offset where each gap starts
            double xMax = 0;
            double y1Max = 0, maxMargin = 0;
            for (int gi = 0; gi < gaps.Count; gi++)
            {
                gapX0[gi] = xMax;
                var g = gaps[gi];
                xMax += g.GapLength;
                if (gi < gaps.Count - 1) xMax += gapPad;
                for (int j = 0; j < g.Distance.Count; j++)
                {
                    if (g.EFalling[j] > y1Max) y1Max = g.EFalling[j];
                    if (g.EAverage[j] > y1Max) y1Max = g.EAverage[j];
                    // E_design spikes as d→0; cap its influence on the scale to the
                    // value at full gap length so the panel stays readable.
                    double dEnd = g.EDesign.Count > 0 ? g.EDesign[^1] : 0;
                    if (!double.IsPositiveInfinity(dEnd) && dEnd > y1Max) y1Max = dEnd;
                    double mg = g.Margin[j];
                    if (!double.IsPositiveInfinity(mg) && !double.IsNaN(mg) && mg > maxMargin) maxMargin = mg;
                }
            }
            if (!(xMax > 0)) xMax = 1;
            if (!(y1Max > 0)) y1Max = 1;
            y1Max *= 1.15;
            // Margin grows large as d→0 (E_design→∞); cap the axis so the design-relevant
            // region around the 1.0 limit stays readable. Always show at least up to 2×.
            const double marginCeil = 4.0;
            double y2Max = Math.Max(2.0, Math.Min(maxMargin, marginCeil) * 1.1);

            const double titleH = 30;
            const double gap = 14;
            double availH = Bounds.Height - titleH - 24; // leave room for footer
            if (availH < 80) return;
            double subH = (availH - gap) / 2.0;

            var top = new Rect(0, titleH, Bounds.Width, subH);
            var bottom = new Rect(0, titleH + subH + gap, Bounds.Width, subH);

            // --- Top panel: |E| / falling / average / withstand ---
            _ = DrawAxes(context, top,
                "Stress: |E| (blue) · E_avg (orange) · withstand E_design(d) (green dashed)  —  kV/mm",
                0, xMax, 0, y1Max, showXTitle: false, out var map1);

            var fallingPen = new Pen(s_fallingBrush, 1.0);
            var avgPen = new Pen(s_avgBrush, 2.2);
            var designPen = new Pen(s_designBrush, 1.8) { DashStyle = new DashStyle(new double[] { 5, 3 }, 0) };
            var sepPen = new Pen(s_sepBrush, 1.0) { DashStyle = new DashStyle(new double[] { 2, 3 }, 0) };
            var stressPen = new Pen(s_stressBrush, 1.6);

            for (int gi = 0; gi < gaps.Count; gi++)
            {
                var g = gaps[gi];
                double x0 = gapX0[gi];
                int m = g.Distance.Count;
                if (m < 1) continue;

                // Gap separator.
                if (gi > 0)
                {
                    double sx = x0 - gapPad * 0.5;
                    context.DrawLine(sepPen, map1(sx, 0), map1(sx, y1Max));
                }

                // Falling function (faint), cumulative average, and distance-dependent
                // withstand E_design(d) — all in falling order over d.
                DrawGapSeries(context, map1, x0, g.Distance, g.EFalling, fallingPen, y1Max);
                DrawGapSeries(context, map1, x0, g.Distance, g.EDesign, designPen, y1Max);
                DrawGapSeries(context, map1, x0, g.Distance, g.EAverage, avgPen, y1Max);

                // Mark the governing point (min margin) on the average curve.
                var gp = map1(x0 + g.GoverningDistance, Math.Min(g.GoverningEAverage, y1Max));
                context.DrawEllipse(s_avgBrush, null, gp, 3.5, 3.5);
            }

            // Actual |E| profile in physical order, mapped onto each gap's axis span so
            // the reader can relate the falling rearrangement back to the real line.
            DrawActualFieldPerGap(context, map1, pts, gaps, gapX0, y1Max, stressPen);

            // --- Bottom panel: cumulative safety margin σ(d) = E_design(d)/E_average(d) ---
            _ = DrawAxes(context, bottom,
                "Safety margin  σ(d) = E_design(d) / E_avg(d)   (governing = minimum)",
                0, xMax, 0, y2Max, showXTitle: true, out var map2);

            // 1.0 limit reference line (margin = 1 is exactly at the withstand limit).
            if (1.0 <= y2Max)
            {
                var limitPen = new Pen(s_limitBrush, 1.4) { DashStyle = new DashStyle(new double[] { 4, 3 }, 0) };
                context.DrawLine(limitPen, map2(0, 1.0), map2(xMax, 1.0));
                DrawText(context, "1.0 (limit)", new Point(map2(xMax, 1.0).X - 70, map2(0, 1.0).Y - 16), 11, s_limitBrush);
            }

            for (int gi = 0; gi < gaps.Count; gi++)
            {
                var g = gaps[gi];
                double x0 = gapX0[gi];
                if (gi > 0)
                {
                    double sx = x0 - gapPad * 0.5;
                    context.DrawLine(sepPen, map2(sx, 0), map2(sx, y2Max));
                }
                DrawColoredMarginGap(context, map2, x0, g.Distance, g.Margin, y2Max);

                // Governing (minimum) margin marker.
                var gp = map2(x0 + g.GoverningDistance, Math.Min(g.GoverningMargin, y2Max));
                context.DrawEllipse(new SolidColorBrush(MarginColor(MarginSeverity(g.GoverningMargin))), null, gp, 3.5, 3.5);
            }

            // Footer with the governing numbers (matches the hover tooltip / table).
            var row = electrostat_UI.ViewModels.StreamlineSummaryRow.FromStreamline(StreamlineNumber, s);
            string footer =
                $"Governing:  E_avg {row.PeakField:F2} kV/mm   vs   E_design {(double.IsPositiveInfinity(row.AllowableField) ? "n/a" : row.AllowableField.ToString("F2") + " kV/mm")}" +
                $"    Margin {row.MarginText}    ({gaps.Count} oil gap{(gaps.Count == 1 ? "" : "s")})";
            DrawText(context, footer, new Point(12, Bounds.Height - 20), 12, s_axisBrush);
        }

        /// <summary>
        /// Draws one gap's falling-order series (Distance is the in-gap running distance,
        /// shifted by the gap's axis offset). Values are clamped to the panel max so the
        /// near-zero withstand spike doesn't blow out the scale.
        /// </summary>
        private static void DrawGapSeries(
            DrawingContext context, Func<double, double, Point> map,
            double x0, IReadOnlyList<double> dist, IReadOnlyList<double> y, IPen pen, double yMax)
        {
            int m = dist.Count;
            if (m < 1) return;
            var geo = new StreamGeometry();
            using (var ctx = geo.Open())
            {
                bool started = false;
                for (int j = 0; j < m; j++)
                {
                    double v = y[j];
                    if (double.IsNaN(v) || double.IsPositiveInfinity(v)) continue;
                    if (v > yMax) v = yMax;
                    var pt = map(x0 + dist[j], v);
                    if (!started) { ctx.BeginFigure(pt, isFilled: false); started = true; }
                    else ctx.LineTo(pt);
                }
                if (started) ctx.EndFigure(false);
            }
            context.DrawGeometry(null, pen, geo);
        }

        /// <summary>
        /// Overlays the actual |E| profile (physical traversal order) within each gap's
        /// axis span, so the real, un-sorted field is visible alongside the falling /
        /// average / withstand curves of the cumulative method.
        /// </summary>
        private static void DrawActualFieldPerGap(
            DrawingContext context, Func<double, double, Point> map,
            IReadOnlyList<StreamlineMarginPoint> pts,
            IReadOnlyList<OilGapCumulativeMargin> gaps,
            double[] gapX0, double yMax, IPen pen)
        {
            int n = pts.Count;
            for (int gi = 0; gi < gaps.Count; gi++)
            {
                var g = gaps[gi];
                // Authoritative physical point span recorded by the calculator.
                int start = g.PointStart;
                int end = Math.Min(g.PointEnd, n);
                if (end - start < 2) continue;

                double x0 = gapX0[gi];
                double run = 0;
                var geo = new StreamGeometry();
                using (var ctx = geo.Open())
                {
                    double vy = Math.Min(pts[start].EMagnitude, yMax);
                    ctx.BeginFigure(map(x0, vy), isFilled: false);
                    for (int k = start + 1; k < end; k++)
                    {
                        double dx = pts[k].X - pts[k - 1].X;
                        double dy = pts[k].Y - pts[k - 1].Y;
                        run += Math.Sqrt(dx * dx + dy * dy);
                        ctx.LineTo(map(x0 + run, Math.Min(pts[k].EMagnitude, yMax)));
                    }
                    ctx.EndFigure(false);
                }
                context.DrawGeometry(null, pen, geo);
            }
        }

        /// <summary>
        /// Draws the panel frame, gridlines, ticks, axis labels and panel title,
        /// returning the inner plotting rectangle and a data→screen mapping function.
        /// </summary>
        private Rect DrawAxes(
            DrawingContext context, Rect outer, string panelTitle,
            double xMin, double xMax, double yMin, double yMax,
            bool showXTitle, out Func<double, double, Point> map)
        {
            const double padLeft = 60;
            const double padRight = 16;
            const double padTop = 22;
            double padBottom = showXTitle ? 38 : 26;

            var inner = new Rect(
                outer.X + padLeft,
                outer.Y + padTop,
                Math.Max(outer.Width - padLeft - padRight, 1),
                Math.Max(outer.Height - padTop - padBottom, 1));

            double xSpan = xMax - xMin; if (xSpan <= 0) xSpan = 1;
            double ySpan = yMax - yMin; if (ySpan <= 0) ySpan = 1;

            Point M(double x, double y) => new(
                inner.X + (x - xMin) / xSpan * inner.Width,
                inner.Bottom - (y - yMin) / ySpan * inner.Height);
            map = M;

            // Panel title.
            DrawText(context, panelTitle, new Point(outer.X + padLeft, outer.Y + 2), 12, s_axisBrush, bold: true);

            var gridPen = new Pen(s_gridBrush, 1);
            var axisPen = new Pen(s_axisBrush, 1.2);

            // Y gridlines + labels.
            foreach (double yt in Ticks(yMin, yMax, 5))
            {
                var a = M(xMin, yt);
                var b = M(xMax, yt);
                context.DrawLine(gridPen, a, b);
                string lbl = FormatTick(yt, yMax);
                var ft = MakeText(lbl, 11, s_axisBrush);
                context.DrawText(ft, new Point(inner.X - 6 - ft.Width, a.Y - ft.Height / 2));
            }

            // X gridlines + labels.
            foreach (double xt in Ticks(xMin, xMax, 6))
            {
                var a = M(xt, yMin);
                var b = M(xt, yMax);
                context.DrawLine(gridPen, a, b);
                string lbl = FormatTick(xt, xMax);
                var ft = MakeText(lbl, 11, s_axisBrush);
                context.DrawText(ft, new Point(a.X - ft.Width / 2, inner.Bottom + 4));
            }

            // Frame.
            context.DrawRectangle(null, axisPen, inner);

            if (showXTitle)
            {
                var ft = MakeText("Arc length (mm)", 12, s_axisBrush);
                context.DrawText(ft, new Point(inner.X + (inner.Width - ft.Width) / 2, inner.Bottom + 18));
            }

            return inner;
        }

        /// <summary>
        /// Draws one gap's cumulative safety-margin curve (falling-order, in-gap distance
        /// shifted by the gap's axis offset) with a green→red ramp keyed to severity
        /// (1/margin), so a small margin reads red. Values are clamped to the panel max.
        /// </summary>
        private static void DrawColoredMarginGap(
            DrawingContext context, Func<double, double, Point> map,
            double x0, IReadOnlyList<double> dist, IReadOnlyList<double> margin, double yMax)
        {
            const int kBuckets = 24;
            var pens = new Pen[kBuckets + 1];
            Pen GetPen(double sev)
            {
                if (double.IsNaN(sev) || sev < 0) sev = 0;
                if (sev > 1) sev = 1;
                int b = (int)Math.Round(sev * kBuckets);
                return pens[b] ??= new Pen(new SolidColorBrush(MarginColor(b / (double)kBuckets)), 2.0);
            }

            double Clamp(double v) => double.IsPositiveInfinity(v) || double.IsNaN(v) ? yMax : Math.Min(v, yMax);

            for (int i = 1; i < dist.Count; i++)
            {
                double sev = 0.5 * (MarginSeverity(margin[i - 1]) + MarginSeverity(margin[i]));
                context.DrawLine(GetPen(sev),
                    map(x0 + dist[i - 1], Clamp(margin[i - 1])),
                    map(x0 + dist[i], Clamp(margin[i])));
            }
        }

        /// <summary>
        /// Maps a safety margin to a 0..1 severity for color-mapping: severity = 1/margin,
        /// clamped to [0, 1]. Margin 1.0 (at the limit) → 1.0 (red); margin ≥ 2 → ≤ 0.5;
        /// +∞ (no constraint) → 0 (green).
        /// </summary>
        private static double MarginSeverity(double margin)
        {
            if (double.IsPositiveInfinity(margin) || double.IsNaN(margin) || margin <= 0) return 0;
            double sev = 1.0 / margin;
            return sev > 1 ? 1 : sev;
        }

        /// <summary>Green → yellow → red ramp for stress severity (0 = safe, 1 = at/over the limit).</summary>
        private static Color MarginColor(double t)
        {
            if (t < 0) t = 0; else if (t > 1) t = 1;
            byte r, g;
            if (t < 0.5)
            {
                double u = t / 0.5;
                r = (byte)Math.Round(255 * u);
                g = 180;
            }
            else
            {
                double u = (t - 0.5) / 0.5;
                r = 255;
                g = (byte)Math.Round(180 * (1 - u));
            }
            return Color.FromArgb(255, r, g, 30);
        }

        // --- small text / tick helpers ---

        private FormattedText MakeText(string text, double size, IBrush brush, bool bold = false)
        {
            var typeface = bold
                ? new Typeface(FontFamily.Default, FontStyle.Normal, FontWeight.SemiBold)
                : Typeface.Default;
            return new FormattedText(text, CultureInfo.CurrentCulture,
                FlowDirection.LeftToRight, typeface, size, brush);
        }

        private void DrawText(DrawingContext context, string text, Point at, double size, IBrush brush, bool bold = false)
            => context.DrawText(MakeText(text, size, brush, bold), at);

        private void DrawCenteredText(DrawingContext context, string text, IBrush brush)
        {
            var ft = MakeText(text, 14, brush);
            context.DrawText(ft, new Point((Bounds.Width - ft.Width) / 2, (Bounds.Height - ft.Height) / 2));
        }

        private static string FormatTick(double v, double range)
        {
            double a = Math.Abs(range);
            if (a >= 100) return v.ToString("F0", CultureInfo.CurrentCulture);
            if (a >= 10) return v.ToString("F1", CultureInfo.CurrentCulture);
            return v.ToString("F2", CultureInfo.CurrentCulture);
        }

        /// <summary>Generates "nice" axis tick values spanning [min, max].</summary>
        private static IEnumerable<double> Ticks(double min, double max, int target)
        {
            if (!(max > min)) { yield return min; yield break; }
            double span = NiceNum(max - min, false);
            double step = NiceNum(span / Math.Max(target - 1, 1), true);
            double start = Math.Ceiling(min / step) * step;
            for (double t = start; t <= max + step * 0.5; t += step)
                yield return Math.Abs(t) < step * 1e-6 ? 0 : t;
        }

        private static double NiceNum(double range, bool round)
        {
            double exp = Math.Floor(Math.Log10(range));
            double f = range / Math.Pow(10, exp);
            double nf;
            if (round)
                nf = f < 1.5 ? 1 : f < 3 ? 2 : f < 7 ? 5 : 10;
            else
                nf = f <= 1 ? 1 : f <= 2 ? 2 : f <= 5 ? 5 : 10;
            return nf * Math.Pow(10, exp);
        }
    }
}
