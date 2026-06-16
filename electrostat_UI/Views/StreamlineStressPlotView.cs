using System;
using System.Collections.Generic;
using Avalonia;
using Avalonia.Collections;
using Avalonia.Controls;
using Avalonia.Controls.Shapes;
using Avalonia.Layout;
using Avalonia.Media;
using electrostat;
using electrostat_UI.ViewModels;
using LiveChartsCore;
using LiveChartsCore.Defaults;
using LiveChartsCore.Measure;
using LiveChartsCore.SkiaSharpView;
using LiveChartsCore.SkiaSharpView.Avalonia;
using LiveChartsCore.SkiaSharpView.Painting;
using LiveChartsCore.SkiaSharpView.Painting.Effects;
using SkiaSharp;

namespace electrostat_UI.Views
{
    /// <summary>
    /// LiveCharts2-based plot of stress and margin along a single
    /// <see cref="StreamlineWithMargin"/>. Two stacked <see cref="CartesianChart"/>s share
    /// an arc-length X axis:
    /// <list type="bullet">
    /// <item>top: local |E| (blue), the falling field <c>E_falling</c> (light grey), the
    /// cumulative average <c>E_average</c> (orange) and the Weidmann withstand
    /// <c>E_design</c> (green dashed), all in kV/mm;</item>
    /// <item>bottom: cumulative safety margin σ(d) = E_design / E_average with the &lt; 1.0
    /// danger zone shaded and the 1.0 limit marked.</item>
    /// </list>
    /// Every series is named so the legend identifies each curve (in particular the
    /// otherwise-cryptic light-grey <c>E_falling</c> rearrangement).
    /// </summary>
    public class StreamlineStressPlotView : UserControl
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

        // The plot palette (series, axes, grid) is tuned for a white background, as in
        // the original hand-drawn view. Force white here so it stays readable regardless
        // of the app's (possibly dark) system theme.
        private static readonly IBrush s_textBrush = new SolidColorBrush(Color.FromRgb(60, 60, 60));

        // Series colors, matching the previous hand-drawn palette.
        private static readonly SKColor s_axisColor = new(60, 60, 60);
        private static readonly SKColor s_stressColor = new(30, 90, 200);   // |E| (actual)
        private static readonly SKColor s_avgColor = new(210, 120, 0);      // E_average
        private static readonly SKColor s_designColor = new(20, 150, 60);   // E_design (withstand)
        private static readonly SKColor s_fallingColor = new(110, 110, 110, 150); // E_falling
        private static readonly SKColor s_limitColor = new(210, 40, 40);
        private static readonly SKColor s_sepColor = new(0, 0, 0, 60);
        private static readonly SKColor s_gridColor = new(0, 0, 0, 28);

        private const double GapPad = 2.0;       // mm of whitespace between gaps on the axis
        private const double MarginCeil = 4.0;   // cap for the safety-margin axis

        public StreamlineStressPlotView()
        {
            // The chart palette is designed for a light background; pin it so the plot
            // does not inherit a dark system theme (which left text unreadable).
            Background = Brushes.White;
            BuildContent();
        }

        protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
        {
            base.OnPropertyChanged(change);
            if (change.Property == StreamlineProperty || change.Property == StreamlineNumberProperty)
                BuildContent();
        }

        private void BuildContent()
        {
            var s = Streamline;
            var pts = s?.Points;

            if (s == null || pts == null || pts.Count < 2)
            {
                Content = CenteredMessage("No streamline data.");
                return;
            }

            string title = $"Streamline #{StreamlineNumber} — cumulative-method stress & margin";
            var gaps = s.OilGaps;

            if (gaps == null || gaps.Count == 0)
            {
                Content = TitledMessage(title,
                    "This streamline has no oil gap — the Weidmann cumulative method does not apply.");
                return;
            }

            // Lay the oil gaps out end-to-end on a single cumulative-distance axis. Each
            // gap's curves run over d ∈ (0, gapLength]; a small pad separates gaps.
            var gapX0 = new double[gaps.Count]; // axis offset where each gap starts
            double xMax = 0;
            double y1Max = 0, maxMargin = 0;
            for (int gi = 0; gi < gaps.Count; gi++)
            {
                gapX0[gi] = xMax;
                var g = gaps[gi];
                xMax += g.GapLength;
                if (gi < gaps.Count - 1) xMax += GapPad;
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
            double y2Max = Math.Max(2.0, Math.Min(maxMargin, MarginCeil) * 1.1);

            var stressChart = BuildStressChart(s, gaps, gapX0, xMax, y1Max);
            var marginChart = BuildMarginChart(gaps, gapX0, xMax, y2Max);

            // Footer with the governing numbers (matches the hover tooltip / table).
            var row = StreamlineSummaryRow.FromStreamline(StreamlineNumber, s);
            string footer =
                $"Governing:  E_avg {row.PeakField:F2} kV/mm   vs   E_design {(double.IsPositiveInfinity(row.AllowableField) ? "n/a" : row.AllowableField.ToString("F2") + " kV/mm")}" +
                $"    Margin {row.MarginText}    ({gaps.Count} oil gap{(gaps.Count == 1 ? "" : "s")})";

            var grid = new Grid
            {
                Margin = new Thickness(8),
                RowDefinitions = new RowDefinitions("Auto,Auto,*,*,Auto"),
            };

            var titleBlock = new TextBlock
            {
                Text = title,
                FontWeight = FontWeight.SemiBold,
                FontSize = 15,
                Foreground = s_textBrush,
                Margin = new Thickness(4, 2, 4, 6),
            };
            Grid.SetRow(titleBlock, 0);

            // Custom legend in its own layout row so it never overlaps plot / grid lines
            // (LiveCharts' in-chart legend is hidden below).
            var legend = BuildLegend();
            Grid.SetRow(legend, 1);

            Grid.SetRow(stressChart, 2);
            Grid.SetRow(marginChart, 3);

            var footerBlock = new TextBlock
            {
                Text = footer,
                FontSize = 12,
                Foreground = s_textBrush,
                Margin = new Thickness(4, 6, 4, 2),
            };
            Grid.SetRow(footerBlock, 4);

            grid.Children.Add(titleBlock);
            grid.Children.Add(legend);
            grid.Children.Add(stressChart);
            grid.Children.Add(marginChart);
            grid.Children.Add(footerBlock);

            Content = grid;
        }

        /// <summary>
        /// Builds a custom legend row (swatch + label per series) as plain Avalonia
        /// controls. Living in its own layout row keeps it clear of the plot area and
        /// gridlines, unlike LiveCharts' in-chart legend which overlapped the curves.
        /// Entries mirror the stress chart's series colors / line styles.
        /// </summary>
        private static Control BuildLegend()
        {
            var panel = new WrapPanel
            {
                Orientation = Orientation.Horizontal,
                Margin = new Thickness(4, 0, 4, 6),
            };

            panel.Children.Add(LegendLine("E_falling", s_fallingColor, dashed: false));
            panel.Children.Add(LegendLine("E_design (withstand)", s_designColor, dashed: true));
            panel.Children.Add(LegendLine("E_average", s_avgColor, dashed: false));
            panel.Children.Add(LegendLine("|E| (actual)", s_stressColor, dashed: false));
            panel.Children.Add(LegendDot("Governing (min margin)", s_avgColor));

            return panel;
        }

        private static Control LegendLine(string text, SKColor color, bool dashed)
        {
            var line = new Line
            {
                StartPoint = new Point(0, 5),
                EndPoint = new Point(26, 5),
                Stroke = ToBrush(color),
                StrokeThickness = 2.5,
                VerticalAlignment = VerticalAlignment.Center,
            };
            if (dashed) line.StrokeDashArray = new AvaloniaList<double> { 3, 2 };

            return LegendItem(line, text);
        }

        private static Control LegendDot(string text, SKColor color)
        {
            var dot = new Ellipse
            {
                Width = 11,
                Height = 11,
                Fill = ToBrush(color),
                VerticalAlignment = VerticalAlignment.Center,
            };
            return LegendItem(dot, text);
        }

        private static Control LegendItem(Control swatch, string text)
        {
            var sp = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                Margin = new Thickness(0, 0, 16, 0),
                Spacing = 6,
                VerticalAlignment = VerticalAlignment.Center,
            };
            sp.Children.Add(swatch);
            sp.Children.Add(new TextBlock
            {
                Text = text,
                FontSize = 13,
                Foreground = s_textBrush,
                VerticalAlignment = VerticalAlignment.Center,
            });
            return sp;
        }

        private static IBrush ToBrush(SKColor c)
            => new SolidColorBrush(Color.FromArgb(c.Alpha, c.Red, c.Green, c.Blue));

        private static CartesianChart BuildStressChart(
            StreamlineWithMargin s, IReadOnlyList<OilGapCumulativeMargin> gaps,
            double[] gapX0, double xMax, double y1Max)
        {
            var fallingPaint = new SolidColorPaint(s_fallingColor) { StrokeThickness = 1f };
            var designPaint = new SolidColorPaint(s_designColor)
            {
                StrokeThickness = 1.8f,
                PathEffect = new DashEffect(new float[] { 6, 3 }),
            };
            var avgPaint = new SolidColorPaint(s_avgColor) { StrokeThickness = 2.2f };
            var stressPaint = new SolidColorPaint(s_stressColor) { StrokeThickness = 1.6f };

            // Governing-point markers sit on the average curve (clamped to the panel max).
            var govPts = new List<ObservablePoint>(gaps.Count);
            for (int gi = 0; gi < gaps.Count; gi++)
                govPts.Add(new ObservablePoint(
                    gapX0[gi] + gaps[gi].GoverningDistance,
                    Math.Min(gaps[gi].GoverningEAverage, y1Max)));

            // Draw order = legend/z order: faint falling first (background), then withstand,
            // average, the actual |E| on top, and finally the governing markers.
            var series = new List<ISeries>
            {
                Line("E_falling", GapPoints(gaps, gapX0, g => g.EFalling), fallingPaint),
                Line("E_design (withstand)", GapPoints(gaps, gapX0, g => g.EDesign), designPaint),
                Line("E_average", GapPoints(gaps, gapX0, g => g.EAverage), avgPaint),
                Line("|E| (actual)", ActualFieldPoints(s.Points, gaps, gapX0), stressPaint),
                new ScatterSeries<ObservablePoint>
                {
                    Name = "Governing (min margin)",
                    Values = govPts,
                    Fill = new SolidColorPaint(s_avgColor),
                    Stroke = null,
                    GeometrySize = 9,
                },
            };

            return new CartesianChart
            {
                Series = series,
                XAxes = new[] { ArcAxis(xMax, showTitle: false) },
                YAxes = new[] { ValueAxis("Stress  (kV/mm)", 0, y1Max) },
                Sections = GapSeparators(gaps, gapX0),
                LegendPosition = LegendPosition.Hidden,
                ZoomMode = ZoomAndPanMode.None,
                DrawMargin = new LiveChartsCore.Measure.Margin(60, 8, 16, 8),
            };
        }

        private static CartesianChart BuildMarginChart(
            IReadOnlyList<OilGapCumulativeMargin> gaps, double[] gapX0, double xMax, double y2Max)
        {
            var marginPaint = new SolidColorPaint(new SKColor(70, 70, 70)) { StrokeThickness = 2f };

            var govPts = new List<ObservablePoint>(gaps.Count);
            var govPaints = new List<SKColor>(gaps.Count);
            for (int gi = 0; gi < gaps.Count; gi++)
            {
                govPts.Add(new ObservablePoint(
                    gapX0[gi] + gaps[gi].GoverningDistance,
                    Math.Min(gaps[gi].GoverningMargin, y2Max)));
                govPaints.Add(MarginColor(gaps[gi].GoverningMargin));
            }

            var series = new List<ISeries>
            {
                Line("σ(d) = E_design / E_avg", GapPoints(gaps, gapX0, g => g.Margin), marginPaint),
                new ScatterSeries<ObservablePoint>
                {
                    Name = "Governing (min margin)",
                    Values = govPts,
                    Fill = new SolidColorPaint(govPaints.Count > 0 ? govPaints[0] : s_limitColor),
                    Stroke = null,
                    GeometrySize = 9,
                },
            };

            var sections = GapSeparators(gaps, gapX0);
            // Danger zone: σ < 1.0 means the average stress meets or exceeds the withstand.
            sections.Add(new RectangularSection
            {
                Yi = 0,
                Yj = 1.0,
                Fill = new SolidColorPaint(new SKColor(210, 40, 40, 26)),
            });
            // 1.0 limit line with label.
            sections.Add(new RectangularSection
            {
                Yi = 1.0,
                Yj = 1.0,
                Stroke = new SolidColorPaint(s_limitColor)
                {
                    StrokeThickness = 1.4f,
                    PathEffect = new DashEffect(new float[] { 4, 3 }),
                },
                Label = "1.0 (limit)",
                LabelPaint = new SolidColorPaint(s_limitColor),
                LabelSize = 11,
            });

            return new CartesianChart
            {
                Series = series,
                XAxes = new[] { ArcAxis(xMax, showTitle: true) },
                YAxes = new[] { ValueAxis("Safety margin  σ(d)", 0, y2Max) },
                Sections = sections,
                LegendPosition = LegendPosition.Hidden,
                ZoomMode = ZoomAndPanMode.None,
                DrawMargin = new LiveChartsCore.Measure.Margin(60, 8, 16, 8),
            };
        }

        // --- series / axis / section builders ---

        private static LineSeries<ObservablePoint> Line(string name, IReadOnlyCollection<ObservablePoint> values, SolidColorPaint stroke)
            => new()
            {
                Name = name,
                Values = values,
                Stroke = stroke,
                Fill = null,
                GeometrySize = 0,
                GeometryStroke = null,
                GeometryFill = null,
                LineSmoothness = 0,
            };

        private static Axis ArcAxis(double xMax, bool showTitle)
            => new()
            {
                Name = showTitle ? "Arc length (mm)" : null,
                NamePaint = showTitle ? new SolidColorPaint(s_axisColor) : null,
                NameTextSize = 12,
                LabelsPaint = new SolidColorPaint(s_axisColor),
                TextSize = 11,
                MinLimit = 0,
                MaxLimit = xMax,
                SeparatorsPaint = new SolidColorPaint(s_gridColor) { StrokeThickness = 1f },
            };

        private static Axis ValueAxis(string name, double min, double max)
            => new()
            {
                Name = name,
                NamePaint = new SolidColorPaint(s_axisColor),
                NameTextSize = 12,
                LabelsPaint = new SolidColorPaint(s_axisColor),
                TextSize = 11,
                MinLimit = min,
                MaxLimit = max,
                SeparatorsPaint = new SolidColorPaint(s_gridColor) { StrokeThickness = 1f },
            };

        private static List<RectangularSection> GapSeparators(IReadOnlyList<OilGapCumulativeMargin> gaps, double[] gapX0)
        {
            var list = new List<RectangularSection>();
            for (int gi = 1; gi < gaps.Count; gi++)
            {
                double sx = gapX0[gi] - GapPad * 0.5;
                list.Add(new RectangularSection
                {
                    Xi = sx,
                    Xj = sx,
                    Stroke = new SolidColorPaint(s_sepColor)
                    {
                        StrokeThickness = 1f,
                        PathEffect = new DashEffect(new float[] { 2, 3 }),
                    },
                });
            }
            return list;
        }

        /// <summary>
        /// Builds one X-Y series from a per-gap falling-order array (Distance is the in-gap
        /// running distance, shifted by each gap's axis offset). Gaps are separated by a
        /// null point so the line breaks between them; NaN/∞ samples also break the line.
        /// </summary>
        private static List<ObservablePoint> GapPoints(
            IReadOnlyList<OilGapCumulativeMargin> gaps, double[] gapX0,
            Func<OilGapCumulativeMargin, IReadOnlyList<double>> select)
        {
            var list = new List<ObservablePoint>();
            for (int gi = 0; gi < gaps.Count; gi++)
            {
                if (gi > 0) list.Add(new ObservablePoint(null, null));
                var g = gaps[gi];
                var ys = select(g);
                double x0 = gapX0[gi];
                for (int j = 0; j < g.Distance.Count; j++)
                {
                    double v = ys[j];
                    if (double.IsNaN(v) || double.IsInfinity(v))
                    {
                        list.Add(new ObservablePoint(null, null));
                        continue;
                    }
                    list.Add(new ObservablePoint(x0 + g.Distance[j], v));
                }
            }
            return list;
        }

        /// <summary>
        /// Overlays the actual |E| profile (physical traversal order) within each gap's
        /// axis span, so the real, un-sorted field is visible alongside the falling /
        /// average / withstand curves of the cumulative method.
        /// </summary>
        private static List<ObservablePoint> ActualFieldPoints(
            IReadOnlyList<StreamlineMarginPoint> pts,
            IReadOnlyList<OilGapCumulativeMargin> gaps, double[] gapX0)
        {
            var list = new List<ObservablePoint>();
            int n = pts.Count;
            for (int gi = 0; gi < gaps.Count; gi++)
            {
                var g = gaps[gi];
                int start = g.PointStart;
                int end = Math.Min(g.PointEnd, n);
                if (end - start < 2) continue;

                if (list.Count > 0) list.Add(new ObservablePoint(null, null));
                double x0 = gapX0[gi];
                double run = 0;
                list.Add(new ObservablePoint(x0, pts[start].EMagnitude));
                for (int k = start + 1; k < end; k++)
                {
                    double dx = pts[k].X - pts[k - 1].X;
                    double dy = pts[k].Y - pts[k - 1].Y;
                    run += Math.Sqrt(dx * dx + dy * dy);
                    list.Add(new ObservablePoint(x0 + run, pts[k].EMagnitude));
                }
            }
            return list;
        }

        /// <summary>
        /// Maps a safety margin to a green→red marker color: margin 1.0 (at the limit) → red,
        /// margin ≥ 2 → toward green, +∞ (no constraint) → green.
        /// </summary>
        private static SKColor MarginColor(double margin)
        {
            double sev = (double.IsInfinity(margin) || double.IsNaN(margin) || margin <= 0)
                ? 0
                : Math.Min(1.0, 1.0 / margin);
            byte r, g;
            if (sev < 0.5)
            {
                double u = sev / 0.5;
                r = (byte)Math.Round(255 * u);
                g = 180;
            }
            else
            {
                double u = (sev - 0.5) / 0.5;
                r = 255;
                g = (byte)Math.Round(180 * (1 - u));
            }
            return new SKColor(r, g, 30);
        }

        // --- fallback message layouts ---

        private static Control CenteredMessage(string text)
            => new TextBlock
            {
                Text = text,
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center,
                FontSize = 14,
                Foreground = s_textBrush,
            };

        private static Control TitledMessage(string title, string message)
        {
            var grid = new Grid
            {
                Margin = new Thickness(8),
                RowDefinitions = new RowDefinitions("Auto,*"),
            };

            var titleBlock = new TextBlock
            {
                Text = title,
                FontWeight = FontWeight.SemiBold,
                FontSize = 15,
                Foreground = s_textBrush,
                Margin = new Thickness(4, 2, 4, 6),
            };
            Grid.SetRow(titleBlock, 0);

            var messageBlock = new TextBlock
            {
                Text = message,
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center,
                FontSize = 14,
                Foreground = s_textBrush,
            };
            Grid.SetRow(messageBlock, 1);

            grid.Children.Add(titleBlock);
            grid.Children.Add(messageBlock);
            return grid;
        }
    }
}
