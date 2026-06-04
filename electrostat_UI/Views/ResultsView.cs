using System;
using System.Collections.Generic;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media;
using Avalonia.Rendering.SceneGraph;
using Avalonia.Skia;
using MeshLib;
using SkiaSharp;
using TfmrLib.FEM;
using GeomGeometry = GeometryLib.Geometry;
using GeometryLib;
using electrostat;

namespace electrostat_UI.Views
{
    /// <summary>
    /// Renders the triangular FEM mesh from a <see cref="FEMSolution"/> and shades each
    /// element by a nodal scalar field (potential V by default).
    /// </summary>
    /// <remarks>
    /// Rendering path: the coloured mesh is built once as a Skia <see cref="SKVertices"/>
    /// triangle soup in world coordinates with per-vertex colours sampled from the
    /// ParaView "cool-to-warm" lookup table. Each frame submits a single
    /// <see cref="SKCanvas.DrawVertices"/> call through Avalonia's
    /// <see cref="ISkiaSharpApiLeaseFeature"/>. This means pan/zoom rasterizes on the
    /// GPU at the current resolution (no pixelation, crisp conductor edges) and costs
    /// one draw call per frame regardless of element count.
    /// </remarks>
    public class ResultsView : Control
    {
        public static readonly StyledProperty<FEMSolution?> SolutionProperty =
            AvaloniaProperty.Register<ResultsView, FEMSolution?>(nameof(Solution));

        public static readonly StyledProperty<string?> FieldNameProperty =
            AvaloniaProperty.Register<ResultsView, string?>(nameof(FieldName), "V");

        public static readonly StyledProperty<bool> ShowMeshProperty =
            AvaloniaProperty.Register<ResultsView, bool>(nameof(ShowMesh), true);

        public static readonly StyledProperty<IReadOnlyList<StreamlineWithMargin>?> StreamlinesProperty =
            AvaloniaProperty.Register<ResultsView, IReadOnlyList<StreamlineWithMargin>?>(nameof(Streamlines));

        public static readonly StyledProperty<bool> ShowStreamlinesProperty =
            AvaloniaProperty.Register<ResultsView, bool>(nameof(ShowStreamlines), true);

        public static readonly StyledProperty<GeomGeometry?> GeometryProperty =
            AvaloniaProperty.Register<ResultsView, GeomGeometry?>(nameof(Geometry));

        public static readonly StyledProperty<bool> ShowGeometryOutlinesProperty =
            AvaloniaProperty.Register<ResultsView, bool>(nameof(ShowGeometryOutlines), true);

        public FEMSolution? Solution
        {
            get => GetValue(SolutionProperty);
            set => SetValue(SolutionProperty, value);
        }

        public string? FieldName
        {
            get => GetValue(FieldNameProperty);
            set => SetValue(FieldNameProperty, value);
        }

        public bool ShowMesh
        {
            get => GetValue(ShowMeshProperty);
            set => SetValue(ShowMeshProperty, value);
        }

        public IReadOnlyList<StreamlineWithMargin>? Streamlines
        {
            get => GetValue(StreamlinesProperty);
            set => SetValue(StreamlinesProperty, value);
        }

        public bool ShowStreamlines
        {
            get => GetValue(ShowStreamlinesProperty);
            set => SetValue(ShowStreamlinesProperty, value);
        }

        public GeomGeometry? Geometry
        {
            get => GetValue(GeometryProperty);
            set => SetValue(GeometryProperty, value);
        }

        public bool ShowGeometryOutlines
        {
            get => GetValue(ShowGeometryOutlinesProperty);
            set => SetValue(ShowGeometryOutlinesProperty, value);
        }

        // View transform (screen pixels).
        private double _zoom = 1.0;
        private Vector _pan = new(0, 0);
        private bool _isPanning;
        private Point _lastPointerPos;

        // Cached GPU geometry (depends only on Solution + FieldName).
        private SKVertices? _cachedVertices;
        private SKPath? _cachedEdgePath;
        private FEMSolution? _cachedSolution;
        private string? _cachedFieldName;

        // Cached world bounds for fitting to the control.
        private float _worldMinX, _worldMinY, _worldMaxX, _worldMaxY;
        private bool _hasWorldBounds;

        // Precomputed colour LUT.
        private static readonly SKColor[] s_colorLut = BuildColorLut();

        static ResultsView()
        {
            AffectsRender<ResultsView>(SolutionProperty, FieldNameProperty, ShowMeshProperty,
                StreamlinesProperty, ShowStreamlinesProperty,
                GeometryProperty, ShowGeometryOutlinesProperty);
        }

        public ResultsView()
        {
            Focusable = true;
            ClipToBounds = true;
        }

        protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
        {
            base.OnPropertyChanged(change);
            if (change.Property == SolutionProperty || change.Property == FieldNameProperty)
            {
                InvalidateGeometry();
            }
            if (change.Property == SolutionProperty)
            {
                ResetView();
            }
        }

        private void InvalidateGeometry()
        {
            _cachedVertices?.Dispose();
            _cachedVertices = null;
            _cachedEdgePath?.Dispose();
            _cachedEdgePath = null;
            _cachedSolution = null;
            _hasWorldBounds = false;
        }

        private void ResetView()
        {
            _zoom = 1.0;
            _pan = new Vector(0, 0);
            InvalidateVisual();
        }

        protected override void OnPointerWheelChanged(PointerWheelEventArgs e)
        {
            base.OnPointerWheelChanged(e);
            var pos = e.GetPosition(this);
            double factor = Math.Pow(1.2, e.Delta.Y);
            double newZoom = Math.Clamp(_zoom * factor, 0.05, 500.0);
            if (newZoom == _zoom) return;

            double k = newZoom / _zoom;
            _pan = new Vector(
                pos.X - (pos.X - _pan.X) * k,
                pos.Y - (pos.Y - _pan.Y) * k);
            _zoom = newZoom;

            InvalidateVisual();
            e.Handled = true;
        }

        protected override void OnPointerPressed(PointerPressedEventArgs e)
        {
            base.OnPointerPressed(e);
            var props = e.GetCurrentPoint(this).Properties;
            if (e.ClickCount >= 2 && props.IsLeftButtonPressed)
            {
                ResetView();
                e.Handled = true;
                return;
            }
            if (props.IsMiddleButtonPressed || props.IsLeftButtonPressed)
            {
                _isPanning = true;
                _lastPointerPos = e.GetPosition(this);
                e.Pointer.Capture(this);
                Cursor = new Cursor(StandardCursorType.SizeAll);
                e.Handled = true;
            }
        }

        protected override void OnPointerMoved(PointerEventArgs e)
        {
            base.OnPointerMoved(e);
            if (!_isPanning) return;
            var pos = e.GetPosition(this);
            var delta = pos - _lastPointerPos;
            _lastPointerPos = pos;
            _pan += delta;
            InvalidateVisual();
        }

        protected override void OnPointerReleased(PointerReleasedEventArgs e)
        {
            base.OnPointerReleased(e);
            if (!_isPanning) return;
            _isPanning = false;
            e.Pointer.Capture(null);
            Cursor = Cursor.Default;
            e.Handled = true;
        }

        public override void Render(DrawingContext context)
        {
            base.Render(context);
            context.FillRectangle(Brushes.White, new Rect(Bounds.Size));

            var sol = Solution;
            var mesh = sol?.Mesh;
            if (sol == null || mesh == null || mesh.Nodes.Count == 0 || mesh.Elements.Count == 0)
            {
                DrawCenteredText(context, "No solution loaded.", Brushes.Gray);
                return;
            }

            EnsureGeometry(sol);
            if (_cachedVertices == null || !_hasWorldBounds) return;

            // World -> screen fit. Recomputed cheaply each frame; cost is negligible
            // versus a single GPU draw.
            const double margin = 12.0;
            double w = Bounds.Width - 2 * margin;
            double h = Bounds.Height - 2 * margin;
            if (w <= 0 || h <= 0) return;

            double dx = Math.Max(_worldMaxX - _worldMinX, 1e-9);
            double dy = Math.Max(_worldMaxY - _worldMinY, 1e-9);
            double fit = Math.Min(w / dx, h / dy);
            double offsetX = margin + (w - dx * fit) / 2.0;
            double offsetY = margin + (h - dy * fit) / 2.0;

            // Compose: world -> fitted screen (Y-flip) -> pan/zoom.
            // s(x) = offsetX + (x - minX) * fit
            // s(y) = H - (offsetY + (y - minY) * fit)
            //      = (H - offsetY + minY*fit) + (-fit) * y
            float sx = (float)fit;
            float tx = (float)(offsetX - _worldMinX * fit);
            float sy = (float)(-fit);
            float ty = (float)(Bounds.Height - offsetY + _worldMinY * fit);

            // Apply pan/zoom on top of the fit.
            float a = (float)(sx * _zoom);
            float b = (float)(tx * _zoom + _pan.X);
            float c = (float)(sy * _zoom);
            float d = (float)(ty * _zoom + _pan.Y);

            var matrix = new SKMatrix
            {
                ScaleX = a, SkewX = 0, TransX = b,
                SkewY = 0, ScaleY = c, TransY = d,
                Persp0 = 0, Persp1 = 0, Persp2 = 1,
            };

            context.Custom(new MeshDrawOperation(
                new Rect(Bounds.Size),
                _cachedVertices,
                ShowMesh ? _cachedEdgePath : null,
                matrix));

            // Geometry outlines overlay (drawn before streamlines so streamlines stay on top).
            var geom = Geometry;
            if (ShowGeometryOutlines && geom != null && geom.Surfaces.Count > 0)
            {
                DrawGeometryOutlines(context, geom, sx, tx, sy, ty);
            }

            // Streamline overlay (Avalonia path on top of the GPU mesh).
            var lines = Streamlines;
            if (ShowStreamlines && lines != null && lines.Count > 0)
            {
                DrawStreamlines(context, lines, sx, tx, sy, ty);
            }
        }

        /// <summary>
        /// Render the boundary curves of every <see cref="GeomSurface"/> in the
        /// geometry as black hairlines, mirroring the outline pass in
        /// <see cref="GeometryView"/>. The first surface (oil/domain box) is included
        /// so the outer extent is visible.
        /// </summary>
        private void DrawGeometryOutlines(
            DrawingContext context,
            GeomGeometry geom,
            float sx, float tx, float sy, float ty)
        {
            using var _ = context.PushTransform(
                Matrix.CreateScale(_zoom, _zoom) * Matrix.CreateTranslation(_pan.X, _pan.Y));

            Point Map(double x, double y) => new(sx * x + tx, sy * y + ty);

            // The transform above scales strokes by _zoom; divide it back out so the
            // outline keeps a constant on-screen thickness regardless of zoom.
            double width = 1.0 / Math.Max(_zoom, 1e-6);
            var stroke = new Pen(
                new SolidColorBrush(Color.FromArgb(220, 0, 0, 0)),
                width);

            foreach (var surf in geom.Surfaces)
            {
                GeometryView.DrawSurface(context, surf, Map, fill: null, stroke);
            }
        }

        private void DrawStreamlines(
            DrawingContext context,
            IReadOnlyList<StreamlineWithMargin> lines,
            float sx, float tx, float sy, float ty)
        {
            // Apply pan/zoom on top of the world->fitted-screen mapping (sx,tx,sy,ty).
            using var _ = context.PushTransform(
                Matrix.CreateScale(_zoom, _zoom) * Matrix.CreateTranslation(_pan.X, _pan.Y));

            Point Map(double x, double y) => new(sx * x + tx, sy * y + ty);

            // Cache pens by quantized stress fraction so we don't allocate per segment.
            // A negative bucket index is used to flag "no Weidmann constraint" (solid /
            // metal regions where the curves don't apply); those segments are drawn in
            // a neutral gray so they don't read as "safe oil".
            const int kBuckets = 32;
            var pens = new Pen[kBuckets + 1];
            Pen? naPen = null;
            // The transform above scales strokes by _zoom; divide it back out so
            // streamlines keep a constant on-screen thickness regardless of zoom.
            double width = 1.5 / Math.Max(_zoom, 1e-6);
            Pen GetPen(double frac, bool applicable)
            {
                if (!applicable)
                {
                    return naPen ??= new Pen(
                        new SolidColorBrush(Color.FromArgb(180, 120, 120, 120)), width);
                }
                if (double.IsNaN(frac) || frac < 0) frac = 0;
                if (frac > 1) frac = 1;
                int b = (int)Math.Round(frac * kBuckets);
                if (pens[b] == null)
                {
                    var c = MarginColor(b / (double)kBuckets);
                    pens[b] = new Pen(new SolidColorBrush(c), width);
                }
                return pens[b];
            }

            foreach (var lm in lines)
            {
                var pts = lm.Points;
                if (pts.Count < 2) continue;

                for (int i = 1; i < pts.Count; i++)
                {
                    var a = pts[i - 1];
                    var bp = pts[i];
                    bool applicable = !double.IsPositiveInfinity(a.EAllow)
                                   && !double.IsPositiveInfinity(bp.EAllow);
                    double frac = 0.5 * (a.StressFraction + bp.StressFraction);
                    context.DrawLine(GetPen(frac, applicable), Map(a.X, a.Y), Map(bp.X, bp.Y));
                }
            }
        }

        /// <summary>
        /// Green → yellow → red ramp for streamline stress fraction (|E| / E_allow).
        /// At fraction == 1 the streamline is at the Wiedmann limit; > 1 saturates red.
        /// </summary>
        private static Color MarginColor(double t)
        {
            if (t < 0) t = 0; else if (t > 1) t = 1;
            byte r, g;
            if (t < 0.5)
            {
                double u = t / 0.5;
                r = (byte)Math.Round(255 * u);
                g = 200;
            }
            else
            {
                double u = (t - 0.5) / 0.5;
                r = 255;
                g = (byte)Math.Round(200 * (1 - u));
            }
            return Color.FromArgb(220, r, g, 30);
        }

        /// <summary>
        /// Build the per-vertex triangle soup (positions + colours) once per
        /// data/field change. Per-vertex colour is sampled from the (nonlinear)
        /// ParaView LUT and then linearly interpolated by the GPU across each
        /// triangle (Gouraud shading) — matches ParaView's default behaviour.
        /// </summary>
        private void EnsureGeometry(FEMSolution sol)
        {
            if (_cachedVertices != null &&
                ReferenceEquals(_cachedSolution, sol) &&
                _cachedFieldName == FieldName)
            {
                return;
            }

            InvalidateGeometry();

            var mesh = sol.Mesh!;

            // Resolve the field to colour by.
            Dictionary<int, double>? nodeField = null;
            Dictionary<int, double>? elemField = null;
            var fieldName = FieldName;

            bool wantEMag = !string.IsNullOrEmpty(fieldName) &&
                            (fieldName!.Equals("|E|", StringComparison.OrdinalIgnoreCase) ||
                             fieldName.Equals("E", StringComparison.OrdinalIgnoreCase) ||
                             fieldName.Equals("Emag", StringComparison.OrdinalIgnoreCase));

            if (wantEMag && TryComputeEMagnitudePerElement(sol, out var eMag))
            {
                elemField = eMag;
            }
            else if (!string.IsNullOrEmpty(fieldName) &&
                     sol.NodalScalars.TryGetValue(fieldName!, out var requested))
            {
                nodeField = requested;
            }
            else if (sol.TryGetPotential(out var pot))
            {
                nodeField = pot;
            }

            // Node id -> position lookup and world bounds.
            var nodes = new Dictionary<int, (float x, float y)>(mesh.Nodes.Count);
            float minX = float.PositiveInfinity, minY = float.PositiveInfinity;
            float maxX = float.NegativeInfinity, maxY = float.NegativeInfinity;
            foreach (var n in mesh.Nodes)
            {
                float nx = (float)n.X;
                float ny = (float)n.Y;
                nodes[(int)n.Id] = (nx, ny);
                if (nx < minX) minX = nx;
                if (nx > maxX) maxX = nx;
                if (ny < minY) minY = ny;
                if (ny > maxY) maxY = ny;
            }
            if (!IsFinite(minX) || !IsFinite(maxX) || !IsFinite(minY) || !IsFinite(maxY))
                return;

            _worldMinX = minX; _worldMinY = minY;
            _worldMaxX = maxX; _worldMaxY = maxY;
            _hasWorldBounds = true;

            // Field range for colour mapping.
            double fMin = 0, fMax = 1;
            if (nodeField != null && nodeField.Count > 0)
            {
                fMin = double.PositiveInfinity;
                fMax = double.NegativeInfinity;
                foreach (var v in nodeField.Values)
                {
                    if (v < fMin) fMin = v;
                    if (v > fMax) fMax = v;
                }
                if (!(fMax > fMin)) fMax = fMin + 1.0;
            }
            else if (elemField != null && elemField.Count > 0)
            {
                fMin = double.PositiveInfinity;
                fMax = double.NegativeInfinity;
                foreach (var v in elemField.Values)
                {
                    if (v < fMin) fMin = v;
                    if (v > fMax) fMax = v;
                }
                if (!(fMax > fMin)) fMax = fMin + 1.0;
            }
            double invRange = 1.0 / (fMax - fMin);
            int lutMax = s_colorLut.Length - 1;
            SKColor defaultColor = new SKColor(200, 200, 200);

            // Build a non-indexed triangle soup. We use SKVertexMode.Triangles with
            // 3 vertices per triangle so each element can have independent colours
            // (required for element-valued fields, and preserves nodal-field colour
            // at every corner without averaging across neighbours).
            int triCount = 0;
            foreach (var elem in mesh.Elements)
            {
                if ((elem.Type == 2 || elem.Type == 9) && elem.Nodes.Count >= 3)
                    triCount++;
            }
            if (triCount == 0) return;

            int vertCount = triCount * 3;
            var positions = new SKPoint[vertCount];
            var colors = new SKColor[vertCount];

            var edgePath = new SKPath();

            int vi = 0;
            foreach (var elem in mesh.Elements)
            {
                if (elem.Type != 2 && elem.Type != 9) continue;
                if (elem.Nodes.Count < 3) continue;

                int id0 = elem.Nodes[0];
                int id1 = elem.Nodes[1];
                int id2 = elem.Nodes[2];
                if (!nodes.TryGetValue(id0, out var p0)) continue;
                if (!nodes.TryGetValue(id1, out var p1)) continue;
                if (!nodes.TryGetValue(id2, out var p2)) continue;

                SKColor c0, c1, c2;
                if (elemField != null)
                {
                    if (!elemField.TryGetValue((int)elem.Id, out var ev)) { c0 = c1 = c2 = defaultColor; }
                    else
                    {
                        double t = (ev - fMin) * invRange;
                        var col = SampleLut(t, lutMax);
                        c0 = c1 = c2 = col;
                    }
                }
                else if (nodeField != null)
                {
                    nodeField.TryGetValue(id0, out var v0);
                    nodeField.TryGetValue(id1, out var v1);
                    nodeField.TryGetValue(id2, out var v2);
                    c0 = SampleLut((v0 - fMin) * invRange, lutMax);
                    c1 = SampleLut((v1 - fMin) * invRange, lutMax);
                    c2 = SampleLut((v2 - fMin) * invRange, lutMax);
                }
                else
                {
                    c0 = c1 = c2 = defaultColor;
                }

                positions[vi] = new SKPoint(p0.x, p0.y); colors[vi++] = c0;
                positions[vi] = new SKPoint(p1.x, p1.y); colors[vi++] = c1;
                positions[vi] = new SKPoint(p2.x, p2.y); colors[vi++] = c2;

                edgePath.MoveTo(p0.x, p0.y);
                edgePath.LineTo(p1.x, p1.y);
                edgePath.LineTo(p2.x, p2.y);
                edgePath.Close();
            }

            if (vi < vertCount)
            {
                // Some elements were skipped (missing node lookup); trim arrays.
                Array.Resize(ref positions, vi);
                Array.Resize(ref colors, vi);
            }

            _cachedVertices = SKVertices.CreateCopy(SKVertexMode.Triangles, positions, colors);
            _cachedEdgePath = edgePath;
            _cachedSolution = sol;
            _cachedFieldName = FieldName;
        }

        private static SKColor SampleLut(double t, int lutMax)
        {
            if (double.IsNaN(t)) return new SKColor(200, 200, 200);
            if (t < 0) t = 0; else if (t > 1) t = 1;
            int idx = (int)(t * lutMax + 0.5);
            return s_colorLut[idx];
        }

        private void DrawCenteredText(DrawingContext context, string text, IBrush brush)
        {
            var ft = new FormattedText(
                text,
                System.Globalization.CultureInfo.CurrentCulture,
                FlowDirection.LeftToRight,
                Typeface.Default,
                14,
                brush);
            context.DrawText(ft,
                new Point((Bounds.Width - ft.Width) / 2, (Bounds.Height - ft.Height) / 2));
        }

        private static bool IsFinite(double v) => !double.IsNaN(v) && !double.IsInfinity(v);

        /// <summary>
        /// ParaView "Cool to Warm" diverging colour map (Kenneth Moreland).
        /// </summary>
        private static SKColor ColorMap(double t)
        {
            if (double.IsNaN(t)) return new SKColor(200, 200, 200);
            if (t < 0) t = 0; else if (t > 1) t = 1;

            ReadOnlySpan<(double p, double r, double g, double b)> stops = stackalloc (double, double, double, double)[]
            {
                (0.0000, 0.2298, 0.2987, 0.7537),
                (0.1250, 0.3026, 0.4079, 0.8424),
                (0.2500, 0.3920, 0.5226, 0.9089),
                (0.3750, 0.4933, 0.6324, 0.9514),
                (0.5000, 0.8654, 0.8654, 0.8654),
                (0.6250, 0.9568, 0.6772, 0.5544),
                (0.7500, 0.9001, 0.5060, 0.3614),
                (0.8750, 0.7883, 0.3438, 0.2317),
                (1.0000, 0.7057, 0.0156, 0.1502),
            };

            for (int i = 1; i < stops.Length; i++)
            {
                if (t <= stops[i].p)
                {
                    var a = stops[i - 1];
                    var b = stops[i];
                    double u = (t - a.p) / (b.p - a.p);
                    double r = a.r + (b.r - a.r) * u;
                    double g = a.g + (b.g - a.g) * u;
                    double bl = a.b + (b.b - a.b) * u;
                    return new SKColor(
                        (byte)Math.Round(Math.Clamp(r, 0, 1) * 255),
                        (byte)Math.Round(Math.Clamp(g, 0, 1) * 255),
                        (byte)Math.Round(Math.Clamp(bl, 0, 1) * 255));
                }
            }
            var last = stops[^1];
            return new SKColor(
                (byte)Math.Round(last.r * 255),
                (byte)Math.Round(last.g * 255),
                (byte)Math.Round(last.b * 255));
        }

        private static SKColor[] BuildColorLut()
        {
            const int N = 256;
            var lut = new SKColor[N];
            for (int i = 0; i < N; i++)
                lut[i] = ColorMap(i / (double)(N - 1));
            return lut;
        }

        /// <summary>
        /// Computes the per-element average |E| if available.
        /// </summary>
        private static bool TryComputeEMagnitudePerElement(
            FEMSolution sol,
            out Dictionary<int, double> magnitudes)
        {
            magnitudes = new Dictionary<int, double>();
            Dictionary<int, ElementNodeField>? source = null;
            foreach (var key in new[] { "E", "ElectricField", "E_field" })
            {
                if (sol.ElementNodalFields.TryGetValue(key, out var f))
                {
                    source = f;
                    break;
                }
            }
            if (source == null) return false;

            foreach (var (elemId, field) in source)
            {
                if (field.NumNodes <= 0 || field.NumComponents <= 0) continue;
                double sum = 0;
                for (int n = 0; n < field.NumNodes; n++)
                {
                    double sq = 0;
                    for (int c = 0; c < field.NumComponents; c++)
                    {
                        double v = field.Get(n, c);
                        sq += v * v;
                    }
                    sum += Math.Sqrt(sq);
                }
                magnitudes[elemId] = sum / field.NumNodes;
            }
            return magnitudes.Count > 0;
        }

        /// <summary>
        /// Avalonia <see cref="ICustomDrawOperation"/> that leases the underlying
        /// Skia canvas and submits a single <see cref="SKCanvas.DrawVertices"/>
        /// call for the entire coloured mesh, followed by an optional edge path
        /// for the mesh overlay.
        /// </summary>
        private sealed class MeshDrawOperation : ICustomDrawOperation
        {
            private readonly Rect _bounds;
            private readonly SKVertices _vertices;
            private readonly SKPath? _edgePath;
            private readonly SKMatrix _matrix;

            public MeshDrawOperation(Rect bounds, SKVertices vertices, SKPath? edgePath, SKMatrix matrix)
            {
                _bounds = bounds;
                _vertices = vertices;
                _edgePath = edgePath;
                _matrix = matrix;
            }

            public Rect Bounds => _bounds;
            public bool HitTest(Point p) => false;
            public bool Equals(ICustomDrawOperation? other) => false;
            public void Dispose() { /* cached geometry is owned by the view */ }

            public void Render(ImmediateDrawingContext context)
            {
                var leaseFeature = context.TryGetFeature<ISkiaSharpApiLeaseFeature>();
                if (leaseFeature == null) return;
                using var lease = leaseFeature.Lease();
                var canvas = lease.SkCanvas;

                canvas.Save();
                try
                {
                    // Pre-concatenate our world->screen matrix with whatever
                    // transform Avalonia has already set on the canvas.
                    canvas.Concat(in _matrix);

                    using (var fillPaint = new SKPaint { IsAntialias = false })
                    {
                        // SKBlendMode.Dst keeps the per-vertex colour as-is (no
                        // modulation by a paint colour). The vertex colours are
                        // sampled from the LUT and interpolated by the GPU.
                        canvas.DrawVertices(_vertices, SKBlendMode.Dst, fillPaint);
                    }

                    if (_edgePath != null)
                    {
                        // Hairline (width 0) draws a 1-device-pixel stroke regardless
                        // of the current matrix scale — exactly what a mesh overlay
                        // wants at any zoom level.
                        using var edgePaint = new SKPaint
                        {
                            Style = SKPaintStyle.Stroke,
                            StrokeWidth = 0,
                            IsAntialias = true,
                            Color = new SKColor(0, 0, 0, 80),
                        };
                        canvas.DrawPath(_edgePath, edgePaint);
                    }
                }
                finally
                {
                    canvas.Restore();
                }
            }
        }
    }
}
