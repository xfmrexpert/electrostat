using System;
using System.Collections.Generic;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media;
using electrostat;
using GeomGeometry = GeometryLib.Geometry;
using GeometryLib;

namespace electrostat_UI.Views
{
    /// <summary>
    /// Renders a <see cref="GeometryLib.Geometry"/> in the r–z plane. The view auto-fits
    /// to the geometry's bounding box and flips the vertical axis so +z points up.
    /// Supports mouse-wheel zoom (about the cursor), middle/left-button drag pan, and
    /// double-click to reset the view.
    /// </summary>
    public class GeometryView : Control
    {
        public static readonly StyledProperty<GeomGeometry?> GeometryProperty =
            AvaloniaProperty.Register<GeometryView, GeomGeometry?>(nameof(Geometry));

        public GeomGeometry? Geometry
        {
            get => GetValue(GeometryProperty);
            set => SetValue(GeometryProperty, value);
        }

        public static readonly StyledProperty<IReadOnlyList<GeomSurface>?> HighlightedSurfacesProperty =
            AvaloniaProperty.Register<GeometryView, IReadOnlyList<GeomSurface>?>(nameof(HighlightedSurfaces));

        public IReadOnlyList<GeomSurface>? HighlightedSurfaces
        {
            get => GetValue(HighlightedSurfacesProperty);
            set => SetValue(HighlightedSurfacesProperty, value);
        }

        public static readonly StyledProperty<IReadOnlyDictionary<GeomSurface, SurfaceCategory>?> SurfaceCategoriesProperty =
            AvaloniaProperty.Register<GeometryView, IReadOnlyDictionary<GeomSurface, SurfaceCategory>?>(nameof(SurfaceCategories));

        /// <summary>
        /// Per-surface semantic classification used to color each region by component type
        /// (winding, pressboard, static-ring paper, …). When a surface is absent the view
        /// falls back to a neutral fill.
        /// </summary>
        public IReadOnlyDictionary<GeomSurface, SurfaceCategory>? SurfaceCategories
        {
            get => GetValue(SurfaceCategoriesProperty);
            set => SetValue(SurfaceCategoriesProperty, value);
        }

        // User-applied transform (relative to the auto-fit baseline).
        // World-to-screen is: screen = (autoFit(world) - _panOrigin) * _zoom + _panOrigin + _panOffset
        // Implemented in Render via a translate-scale-translate composition.
        private double _zoom = 1.0;
        private Vector _pan = new(0, 0);

        private bool _isPanning;
        private Point _lastPointerPos;

        static GeometryView()
        {
            AffectsRender<GeometryView>(GeometryProperty);
            AffectsRender<GeometryView>(HighlightedSurfacesProperty);
            AffectsRender<GeometryView>(SurfaceCategoriesProperty);
        }

        public GeometryView()
        {
            Focusable = true;
            ClipToBounds = true;
        }

        protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
        {
            base.OnPropertyChanged(change);
            if (change.Property == GeometryProperty)
            {
                ResetView();
            }
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
            double newZoom = Math.Clamp(_zoom * factor, 0.05, 200.0);
            if (newZoom == _zoom) return;

            // Keep the world point under the cursor stationary on screen.
            // screen = (auto + _pan) scaled about origin by _zoom is NOT what we do; we
            // apply the transform `Translate(_pan) * ScaleAt(_zoom, pos)` in Render, so
            // adjusting _pan around the cursor preserves it under both old and new zoom:
            //   pos = (pos - _pan_old) * (newZoom / _zoom) + _pan_new + ...  →
            //   _pan_new = pos - (pos - _pan_old) * (newZoom / _zoom)
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

            // Background
            context.FillRectangle(Brushes.White, new Rect(Bounds.Size));

            var geom = Geometry;
            if (geom == null || geom.Surfaces.Count == 0)
                return;

            // Compute world bounds across all surface boundaries.
            double minX = double.PositiveInfinity, minY = double.PositiveInfinity;
            double maxX = double.NegativeInfinity, maxY = double.NegativeInfinity;
            foreach (var surf in geom.Surfaces)
            {
                var b = surf.Boundary.GetBoundingBox();
                if (b.minX < minX) minX = b.minX;
                if (b.minY < minY) minY = b.minY;
                if (b.maxX > maxX) maxX = b.maxX;
                if (b.MaxY > maxY) maxY = b.MaxY;
            }
            if (!IsFinite(minX) || !IsFinite(minY) || !IsFinite(maxX) || !IsFinite(maxY))
                return;

            const double margin = 12.0;
            double w = Bounds.Width - 2 * margin;
            double h = Bounds.Height - 2 * margin;
            if (w <= 0 || h <= 0) return;

            double dx = Math.Max(maxX - minX, 1e-9);
            double dy = Math.Max(maxY - minY, 1e-9);
            double scale = Math.Min(w / dx, h / dy);

            double offsetX = margin + (w - dx * scale) / 2.0;
            double offsetY = margin + (h - dy * scale) / 2.0;

            Point Map(double x, double y) =>
                new Point(offsetX + (x - minX) * scale,
                          // flip Y so +z is up
                          Bounds.Height - (offsetY + (y - minY) * scale));

            // Compose user pan/zoom on top of the auto-fit mapping.
            // Order matters: scale first (about origin), then translate by the pan vector.
            var transform = Matrix.CreateScale(_zoom, _zoom) * Matrix.CreateTranslation(_pan.X, _pan.Y);
            using var _ = context.PushTransform(transform);

            // Compensate stroke thickness so outlines stay 1px on screen at any zoom.
            double strokeWidth = 1.0 / _zoom;

            // First pass: fill surfaces (skip the first surface, which is the oil/domain
            // bounding box; rendering it would obscure all the holes).
            var categories = SurfaceCategories;
            for (int i = 0; i < geom.Surfaces.Count; i++)
            {
                var surf = geom.Surfaces[i];
                if (i == 0) continue;

                var fill = PickFill(surf, categories);
                DrawSurface(context, surf, Map, fill, null);
            }

            // Outline pass on top so boundaries stay crisp.
            var stroke = new Pen(Brushes.Black, strokeWidth);
            foreach (var surf in geom.Surfaces)
            {
                DrawSurface(context, surf, Map, null, stroke);
            }

            // Highlight pass: draw selected surfaces with a bold accent stroke + tinted fill
            // on top of everything else so they pop even when unselected fills overlap.
            var highlights = HighlightedSurfaces;
            if (highlights != null && highlights.Count > 0)
            {
                var hiFill = new SolidColorBrush(Color.FromArgb(80, 255, 140, 0));
                var hiPen = new Pen(new SolidColorBrush(Color.FromArgb(255, 255, 80, 0)),
                                    3.0 / _zoom);
                foreach (var surf in highlights)
                {
                    if (surf == null) continue;
                    DrawSurface(context, surf, Map, hiFill, hiPen);
                }
            }
        }

        private static bool IsFinite(double v) => !double.IsNaN(v) && !double.IsInfinity(v);

        // Semi-transparent fills keyed by what each region physically represents, so
        // components read by material/type at a glance rather than by a rotating palette.
        private static readonly IBrush WindingFill = new SolidColorBrush(Color.FromArgb(140, 184, 115, 51));      // copper
        private static readonly IBrush PressboardFill = new SolidColorBrush(Color.FromArgb(150, 120, 72, 40));    // dark brown
        private static readonly IBrush AngleRingFill = new SolidColorBrush(Color.FromArgb(150, 120, 72, 40));     // dark brown
        private static readonly IBrush StaticPaperFill = new SolidColorBrush(Color.FromArgb(140, 205, 170, 125)); // light brown / tan
        private static readonly IBrush StaticMetalFill = new SolidColorBrush(Color.FromArgb(150, 150, 150, 150)); // gray metal
        private static readonly IBrush OilFill = new SolidColorBrush(Color.FromArgb(40, 230, 220, 160));          // pale oil
        private static readonly IBrush DefaultFill = new SolidColorBrush(Color.FromArgb(120, 100, 149, 237));     // cornflower fallback

        private static IBrush PickFill(GeomSurface surf, IReadOnlyDictionary<GeomSurface, SurfaceCategory>? categories)
        {
            // Color each region by its component type. Surfaces without a known category
            // (e.g. a geometry shown without build metadata) fall back to a neutral fill.
            if (categories != null && categories.TryGetValue(surf, out var category))
            {
                return category switch
                {
                    SurfaceCategory.Winding => WindingFill,
                    SurfaceCategory.Pressboard => PressboardFill,
                    SurfaceCategory.AngleRing => AngleRingFill,
                    SurfaceCategory.StaticRingPaper => StaticPaperFill,
                    SurfaceCategory.StaticRingMetal => StaticMetalFill,
                    SurfaceCategory.Oil => OilFill,
                    _ => DefaultFill,
                };
            }
            return DefaultFill;
        }

        internal static void DrawSurface(
            DrawingContext context,
            GeomSurface surf,
            Func<double, double, Point> map,
            IBrush? fill,
            IPen? stroke)
        {
            var sg = new StreamGeometry();
            using (var ctx = sg.Open())
            {
                AppendLoop(ctx, surf.Boundary, map, isHole: false);
                foreach (var hole in surf.Holes)
                {
                    AppendLoop(ctx, hole, map, isHole: true);
                }
            }

            context.DrawGeometry(fill, stroke, sg);
        }

        internal static void AppendLoop(
            StreamGeometryContext ctx,
            GeomLineLoop loop,
            Func<double, double, Point> map,
            bool isHole)
        {
            var segments = OrderedSegments(loop);
            if (segments.Count == 0) return;

            // Sample world-space points along each segment, preserving direction.
            var worldPts = new List<(double x, double y)>(segments.Count * 4);
            worldPts.Add((segments[0].start.x, segments[0].start.y));

            foreach (var seg in segments)
            {
                if (seg.entity is GeomArc arc)
                {
                    AppendArcSamples(arc, seg.reversed, worldPts);
                }
                else
                {
                    worldPts.Add((seg.end.x, seg.end.y));
                }
            }

            ctx.BeginFigure(map(worldPts[0].x, worldPts[0].y), isFilled: true);
            for (int i = 1; i < worldPts.Count; i++)
            {
                ctx.LineTo(map(worldPts[i].x, worldPts[i].y));
            }
            ctx.EndFigure(true);
        }

        /// <summary>
        /// Tessellate an arc into a polyline. Honors traversal direction by sampling
        /// from the segment's start to its end angle around the arc's center.
        /// </summary>
        private static void AppendArcSamples(
            GeomArc arc,
            bool reversed,
            List<(double x, double y)> outPts)
        {
            var center = arc.Center;
            double radius = arc.Radius;
            if (radius <= 0)
            {
                var endPt = reversed ? arc.StartPt : arc.EndPt;
                outPts.Add((endPt.x, endPt.y));
                return;
            }

            // Compute start/end angles relative to the center, in world coords.
            double aStart = Math.Atan2(arc.StartPt.y - center.y, arc.StartPt.x - center.x);
            double aEnd   = Math.Atan2(arc.EndPt.y   - center.y, arc.EndPt.x   - center.x);

            // Normalize sweep to match the recorded SweepAngle's sign so we go the
            // right way around (matches GeomArc(start, center, end) construction).
            double delta = aEnd - aStart;
            double sweepSign = Math.Sign(arc.SweepAngle != 0 ? arc.SweepAngle : delta);
            if (sweepSign == 0) sweepSign = 1;
            // Force `delta` to have the same sign as sweepSign and magnitude in (0, 2π].
            while (delta * sweepSign <= 0) delta += sweepSign * 2.0 * Math.PI;
            while (Math.Abs(delta) > 2.0 * Math.PI) delta -= sweepSign * 2.0 * Math.PI;

            // If this segment is being traversed in reverse, flip the angular sweep.
            double from = reversed ? aEnd : aStart;
            double sweep = reversed ? -delta : delta;

            // Choose a sample count proportional to arc length, with sane bounds.
            int n = Math.Clamp((int)Math.Ceiling(Math.Abs(sweep) / (Math.PI / 36.0)), 4, 128);

            for (int i = 1; i <= n; i++)
            {
                double t = (double)i / n;
                double a = from + sweep * t;
                outPts.Add((center.x + radius * Math.Cos(a),
                            center.y + radius * Math.Sin(a)));
            }
        }

        /// <summary>
        /// Walk the loop's entities and return them in traversal order, each tagged
        /// with the start/end <see cref="GeomPoint"/> appropriate for the direction
        /// the loop visits them in (some entities are stored against their natural
        /// orientation and need to be reversed when walked).
        /// </summary>
        private static List<(GeomEntity entity, GeomPoint start, GeomPoint end, bool reversed)>
            OrderedSegments(GeomLineLoop loop)
        {
            var result = new List<(GeomEntity, GeomPoint, GeomPoint, bool)>(loop.Boundary.Count);
            GeomPoint? last = null;

            foreach (var entity in loop.Boundary)
            {
                GeomPoint? a = null, b = null;
                switch (entity)
                {
                    case GeomLine l: a = l.pt1; b = l.pt2; break;
                    case GeomArc arc: a = arc.StartPt; b = arc.EndPt; break;
                }
                if (a == null || b == null) continue;

                bool reversed;
                if (last == null)
                {
                    reversed = false;
                }
                else if (ReferenceEquals(a, last))
                {
                    reversed = false;
                }
                else if (ReferenceEquals(b, last))
                {
                    reversed = true;
                }
                else
                {
                    // Disconnected; assume natural orientation.
                    reversed = false;
                }

                var (s, e) = reversed ? (b, a) : (a, b);
                result.Add((entity, s, e, reversed));
                last = e;
            }

            return result;
        }
    }
}
