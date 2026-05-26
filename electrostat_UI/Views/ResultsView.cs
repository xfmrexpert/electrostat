using System;
using System.Collections.Generic;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media;
using MeshLib;
using TfmrLib.FEM;

namespace electrostat_UI.Views
{
    /// <summary>
    /// Renders the triangular FEM mesh from a <see cref="FEMSolution"/> and shades each
    /// element by a nodal scalar field (potential V by default). Mirrors the pan / zoom
    /// behaviour of <see cref="GeometryView"/>.
    /// </summary>
    public class ResultsView : Control
    {
        public static readonly StyledProperty<FEMSolution?> SolutionProperty =
            AvaloniaProperty.Register<ResultsView, FEMSolution?>(nameof(Solution));

        public static readonly StyledProperty<string?> FieldNameProperty =
            AvaloniaProperty.Register<ResultsView, string?>(nameof(FieldName), "V");

        public static readonly StyledProperty<bool> ShowMeshProperty =
            AvaloniaProperty.Register<ResultsView, bool>(nameof(ShowMesh), true);

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

        private double _zoom = 1.0;
        private Vector _pan = new(0, 0);
        private bool _isPanning;
        private Point _lastPointerPos;

        static ResultsView()
        {
            AffectsRender<ResultsView>(SolutionProperty, FieldNameProperty, ShowMeshProperty);
        }

        public ResultsView()
        {
            Focusable = true;
            ClipToBounds = true;
        }

        protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
        {
            base.OnPropertyChanged(change);
            if (change.Property == SolutionProperty)
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

            // Pick the field to colour by.
            Dictionary<int, double>? nodeField = null;
            var fieldName = FieldName;
            if (!string.IsNullOrEmpty(fieldName) &&
                sol.NodalScalars.TryGetValue(fieldName!, out var requested))
            {
                nodeField = requested;
            }
            else if (sol.TryGetPotential(out var pot))
            {
                nodeField = pot;
                fieldName = "V";
            }

            // Build a node-id -> position lookup.
            var nodes = new Dictionary<int, (double x, double y)>(mesh.Nodes.Count);
            double minX = double.PositiveInfinity, minY = double.PositiveInfinity;
            double maxX = double.NegativeInfinity, maxY = double.NegativeInfinity;
            foreach (var n in mesh.Nodes)
            {
                nodes[(int)n.Id] = (n.X, n.Y);
                if (n.X < minX) minX = n.X;
                if (n.X > maxX) maxX = n.X;
                if (n.Y < minY) minY = n.Y;
                if (n.Y > maxY) maxY = n.Y;
            }
            if (!IsFinite(minX) || !IsFinite(maxX) || !IsFinite(minY) || !IsFinite(maxY))
                return;

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
                          Bounds.Height - (offsetY + (y - minY) * scale));

            var transform = Matrix.CreateScale(_zoom, _zoom) * Matrix.CreateTranslation(_pan.X, _pan.Y);
            using var _ = context.PushTransform(transform);

            double strokeWidth = 0.5 / _zoom;
            IPen? meshPen = ShowMesh
                ? new Pen(new SolidColorBrush(Color.FromArgb(80, 0, 0, 0)), strokeWidth)
                : null;

            // Render triangles. Gmsh element types: 2 = 3-node triangle, 9 = 6-node tri.
            foreach (var elem in mesh.Elements)
            {
                int[] cornerLocalIds = elem.Type switch
                {
                    2 => new[] { 0, 1, 2 },
                    9 => new[] { 0, 1, 2 }, // 6-node tri: first 3 nodes are corners
                    _ => Array.Empty<int>(),
                };
                if (cornerLocalIds.Length == 0) continue;
                if (elem.Nodes.Count < cornerLocalIds.Length) continue;

                if (!nodes.TryGetValue(elem.Nodes[cornerLocalIds[0]], out var p0)) continue;
                if (!nodes.TryGetValue(elem.Nodes[cornerLocalIds[1]], out var p1)) continue;
                if (!nodes.TryGetValue(elem.Nodes[cornerLocalIds[2]], out var p2)) continue;

                IBrush? fill = null;
                if (nodeField != null)
                {
                    double v0 = nodeField.TryGetValue(elem.Nodes[cornerLocalIds[0]], out var a) ? a : double.NaN;
                    double v1 = nodeField.TryGetValue(elem.Nodes[cornerLocalIds[1]], out var b) ? b : double.NaN;
                    double v2 = nodeField.TryGetValue(elem.Nodes[cornerLocalIds[2]], out var c) ? c : double.NaN;
                    if (!double.IsNaN(v0) && !double.IsNaN(v1) && !double.IsNaN(v2))
                    {
                        double avg = (v0 + v1 + v2) / 3.0;
                        double t = (avg - fMin) / (fMax - fMin);
                        fill = new SolidColorBrush(ColorMap(t));
                    }
                }

                var sg = new StreamGeometry();
                using (var ctx = sg.Open())
                {
                    ctx.BeginFigure(Map(p0.x, p0.y), isFilled: fill != null);
                    ctx.LineTo(Map(p1.x, p1.y));
                    ctx.LineTo(Map(p2.x, p2.y));
                    ctx.EndFigure(true);
                }
                context.DrawGeometry(fill, meshPen, sg);
            }

            // End-user transform pop happens via 'using' above; draw legend / status in
            // screen-space afterward by exiting that scope.
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
        /// Simple blue -> cyan -> green -> yellow -> red colour ramp for t in [0, 1].
        /// </summary>
        private static Color ColorMap(double t)
        {
            if (double.IsNaN(t)) return Color.FromArgb(255, 200, 200, 200);
            t = Math.Clamp(t, 0.0, 1.0);
            double r, g, b;
            if (t < 0.25)
            {
                double u = t / 0.25;
                r = 0; g = u; b = 1;
            }
            else if (t < 0.5)
            {
                double u = (t - 0.25) / 0.25;
                r = 0; g = 1; b = 1 - u;
            }
            else if (t < 0.75)
            {
                double u = (t - 0.5) / 0.25;
                r = u; g = 1; b = 0;
            }
            else
            {
                double u = (t - 0.75) / 0.25;
                r = 1; g = 1 - u; b = 0;
            }
            return Color.FromArgb(255, (byte)(r * 255), (byte)(g * 255), (byte)(b * 255));
        }
    }
}
