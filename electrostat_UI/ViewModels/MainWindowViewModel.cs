using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Threading.Tasks;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using electrostat;
using electrostat_UI.ViewModels.Components;
using GeometryLib;
using TfmrLib.FEM;

namespace electrostat_UI.ViewModels
{
    public partial class MainWindowViewModel : ViewModelBase
    {
        public ObservableCollection<ElectrostatCase> Examples { get; }

        public ObservableCollection<CaseTreeNode> CaseTree { get; } = new();

        [ObservableProperty]
        private ElectrostatCase? _selectedExample;

        [ObservableProperty]
        private ElectrostatCaseViewModel? _editingCase;

        [ObservableProperty]
        private CaseTreeNode? _selectedTreeNode;

        [ObservableProperty]
        private object? _selectedComponent;

        [ObservableProperty]
        private IReadOnlyList<GeomSurface>? _highlightedSurfaces;

        [ObservableProperty]
        private Geometry? _currentGeometry;

        [ObservableProperty]
        private FEMSolution? _currentSolution;

        [ObservableProperty]
        private string _statusMessage = "Select an example to render its geometry.";

        [ObservableProperty]
        private bool _isSolving;

        public string[] AvailableFields { get; } = new[] { "V", "|E|" };

        [ObservableProperty]
        private string _selectedField = "V";

        [ObservableProperty]
        private bool _showResultsMesh = false;

        [ObservableProperty]
        private bool _showResultsOutlines = true;

        [ObservableProperty]
        private IReadOnlyList<StreamlineWithMargin>? _streamlines;

        [ObservableProperty]
        private bool _showStreamlines = true;

        /// <summary>Per-streamline stress metrics shown in the Streamline Stress tab.</summary>
        public ObservableCollection<StreamlineSummaryRow> StreamlineSummary { get; } = new();

        // Coalesces rapid edits (e.g. while dragging a NumericUpDown spinner) into a
        // single geometry rebuild so the UI stays responsive.
        private readonly DispatcherTimer _rebuildTimer;

        public MainWindowViewModel()
        {
            Examples = new ObservableCollection<ElectrostatCase>(electrostat.Examples.All());

            _rebuildTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(75) };
            _rebuildTimer.Tick += (_, _) => { _rebuildTimer.Stop(); RebuildGeometry(); };

            if (Examples.Count > 0)
            {
                SelectedExample = Examples[0];
            }
        }

        partial void OnSelectedExampleChanged(ElectrostatCase? value)
        {
            CurrentSolution = null;
            Streamlines = null;
            SolveCommand.NotifyCanExecuteChanged();

            if (EditingCase != null)
                EditingCase.Changed -= OnCaseChanged;

            EditingCase = value != null ? new ElectrostatCaseViewModel(value) : null;

            if (EditingCase != null)
                EditingCase.Changed += OnCaseChanged;

            RebuildCaseTree();
            RebuildGeometry();
        }

        partial void OnStreamlinesChanged(IReadOnlyList<StreamlineWithMargin>? value)
        {
            StreamlineSummary.Clear();
            if (value == null) return;

            // The list arrives pre-sorted worst-first by minimum safety margin (see
            // BuildStreamlines), so number rows by list position. This 1-based index is
            // the same one the Results-plot hover tooltip reports, keeping the two views
            // in sync.
            int index = 1;
            foreach (var s in value)
                StreamlineSummary.Add(StreamlineSummaryRow.FromStreamline(index++, s));
        }

        private void OnCaseChanged()
        {
            // Debounce: restart the timer on every change.
            _rebuildTimer.Stop();
            _rebuildTimer.Start();
            RebuildCaseTree();
        }

        partial void OnSelectedTreeNodeChanged(CaseTreeNode? value)
        {
            SelectedComponent = value?.Model;
            UpdateHighlight();
        }

        private void UpdateHighlight()
        {
            var key = SelectedComponent switch
            {
                ComponentViewModel cv => cv.SurfaceKey,
                _ => null,
            };

            if (key == null ||
                !GeometryBuilder.ComponentSurfaces.TryGetValue(key, out var list))
            {
                HighlightedSurfaces = null;
                return;
            }

            // Filter to surfaces still present in the current geometry (some may have
            // been removed after clipping / electrode-interior pruning).
            var current = CurrentGeometry;
            if (current == null)
            {
                HighlightedSurfaces = list;
                return;
            }
            var live = new HashSet<GeomSurface>(current.Surfaces, ReferenceEqualityComparer.Instance);
            HighlightedSurfaces = list.Where(s => live.Contains(s)).ToList();
        }

        private void RebuildGeometry()
        {
            var ec = EditingCase;
            if (ec == null)
            {
                CurrentGeometry = null;
                HighlightedSurfaces = null;
                StatusMessage = "No case loaded.";
                return;
            }

            try
            {
                var model = ec.ToModel();
                CurrentGeometry = GeometryBuilder.BuildGeometryOnly(model, clipToDomain: true);
                StatusMessage = $"{model.Name}: {CurrentGeometry.Surfaces.Count} surfaces";
                UpdateHighlight();
            }
            catch (Exception ex)
            {
                CurrentGeometry = null;
                HighlightedSurfaces = null;
                StatusMessage = $"Failed to build {ec.Name}: {ex.Message}";
            }
        }

        [RelayCommand]
        private void SelectExample(ElectrostatCase? example)
        {
            if (example != null)
                SelectedExample = example;
        }

        // ---- Add / Remove component commands ----

        [RelayCommand]
        private void AddWinding()
        {
            if (EditingCase == null) return;
            EditingCase.Windings.Add(new WindingViewModel
            {
                Name = UniqueName("Winding", EditingCase.Windings.Select(x => x.Name)),
                R0 = 100, ZBottom = 0, Width = 50, Height = 200, FilletR = 1
            });
        }

        [RelayCommand]
        private void AddPressboard()
        {
            if (EditingCase == null) return;
            EditingCase.Pressboards.Add(new PressboardViewModel
            {
                Name = UniqueName("PB", EditingCase.Pressboards.Select(x => x.Name)),
                R0 = 100, ZBottom = 0, Thickness = 3, Height = 200
            });
        }

        [RelayCommand]
        private void AddAngleRing()
        {
            if (EditingCase == null) return;
            EditingCase.AngleRings.Add(new AngleRingViewModel
            {
                Name = UniqueName("AR", EditingCase.AngleRings.Select(x => x.Name)),
                R0 = 100, ZCorner = 0, Tv = 3, Hv = 50, Th = 3, Wh = 50, InsideFilletR = 5
            });
        }

        [RelayCommand]
        private void AddStaticRing()
        {
            if (EditingCase == null) return;
            EditingCase.StaticRings.Add(new StaticRingViewModel
            {
                Name = UniqueName("SR", EditingCase.StaticRings.Select(x => x.Name)),
                R0 = 100, ZBottom = 0, Width = 30, Height = 12, TPaper = 2
            });
        }

        [RelayCommand]
        private void RemoveSelected()
        {
            if (EditingCase == null) return;
            switch (SelectedComponent)
            {
                case WindingViewModel w: EditingCase.Windings.Remove(w); break;
                case PressboardViewModel p: EditingCase.Pressboards.Remove(p); break;
                case AngleRingViewModel a: EditingCase.AngleRings.Remove(a); break;
                case StaticRingViewModel s: EditingCase.StaticRings.Remove(s); break;
            }
        }

        private static string UniqueName(string baseName, IEnumerable<string> existing)
        {
            var set = new HashSet<string>(existing, StringComparer.OrdinalIgnoreCase);
            int i = 1;
            string n;
            do { n = $"{baseName}_{i++}"; } while (set.Contains(n));
            return n;
        }

        private void RebuildCaseTree()
        {
            CaseTree.Clear();
            var ec = EditingCase;
            if (ec == null) return;

            var root = new CaseTreeNode(ec.Name, ec);

            root.Children.Add(new CaseTreeNode("Domain", ec.Domain));

            var w = new CaseTreeNode($"Windings ({ec.Windings.Count})");
            foreach (var x in ec.Windings) w.Children.Add(new CaseTreeNode(x.Name, x));
            root.Children.Add(w);

            var p = new CaseTreeNode($"Pressboards ({ec.Pressboards.Count})");
            foreach (var x in ec.Pressboards) p.Children.Add(new CaseTreeNode(x.Name, x));
            root.Children.Add(p);

            var a = new CaseTreeNode($"Angle Rings ({ec.AngleRings.Count})");
            foreach (var x in ec.AngleRings) a.Children.Add(new CaseTreeNode(x.Name, x));
            root.Children.Add(a);

            var s = new CaseTreeNode($"Static Rings ({ec.StaticRings.Count})");
            foreach (var x in ec.StaticRings) s.Children.Add(new CaseTreeNode(x.Name, x));
            root.Children.Add(s);

            CaseTree.Add(root);
        }

        private bool CanSolve() => EditingCase != null && !IsSolving;

        [RelayCommand(CanExecute = nameof(CanSolve))]
        private async Task SolveAsync()
        {
            var ec = EditingCase;
            if (ec == null) return;

            IsSolving = true;
            SolveCommand.NotifyCanExecuteChanged();
            var ex = ec.ToModel();
            StatusMessage = $"Solving {ex.Name}...";

            try
            {
                var caseName = SafeName(ex.Name);
                var problem = await Task.Run(() =>
                    GeometryBuilder.BuildAndSolve(ex, caseName, lc: 5.0, clipToDomain: true));

                CurrentGeometry = GeometryBuilder.geometry;
                UpdateHighlight();

                CurrentSolution = problem?.Solution;
                if (CurrentSolution == null)
                {
                    Streamlines = null;
                    var reason = (problem as MFEMProblem)?.LastLoadError;
                    var resultsPath = (problem as MFEMProblem)?.ResultsFile;
                    StatusMessage = $"Solve completed but no results were loaded for {ex.Name}. " +
                                    (reason ?? $"Expected results at '{resultsPath ?? "<unset>"}'.");
                }
                else
                {
                    Streamlines = await Task.Run(() =>
                        BuildStreamlines(CurrentSolution!, CurrentGeometry));
                    StatusMessage = $"Solved {ex.Name}. Nodal views: {CurrentSolution.NodalScalars.Count}, " +
                                    $"elements: {CurrentSolution.Mesh?.Elements.Count ?? 0}, " +
                                    $"streamlines: {Streamlines?.Count ?? 0}.";
                }
            }
            catch (Exception exc)
            {
                CurrentSolution = null;
                StatusMessage = $"Solve failed: {exc.Message}";
            }
            finally
            {
                IsSolving = false;
                SolveCommand.NotifyCanExecuteChanged();
            }
        }

        partial void OnIsSolvingChanged(bool value) => SolveCommand.NotifyCanExecuteChanged();

        private static string SafeName(string name)
        {
            var sb = new System.Text.StringBuilder(name.Length);
            foreach (var c in name)
                sb.Append(char.IsLetterOrDigit(c) ? c : '_');
            return sb.ToString();
        }

        /// <summary>
        /// Generate a representative set of streamlines through the solved domain by
        /// seeding from a coarse grid and tracing forward + backward along E. Lines
        /// terminate when they cross a conductor boundary or leave the meshed domain.
        /// </summary>
        private static IReadOnlyList<StreamlineWithMargin> BuildStreamlines(FEMSolution sol, Geometry? geom)
        {
            var lines = new List<StreamlineWithMargin>();
            if (sol.Mesh == null || sol.Mesh.Nodes.Count == 0) return lines;

            FemFieldSampler sampler;
            try { sampler = new FemFieldSampler(sol); }
            catch { return lines; }

            var bounds = sampler.Locator.Bounds;
            double dx = bounds.MaxX - bounds.MinX;
            double dy = bounds.MaxY - bounds.MinY;
            if (dx <= 0 || dy <= 0) return lines;

            // Step size scales with the domain so trace cost is bounded across cases.
            double diag = Math.Sqrt(dx * dx + dy * dy);
            double step = Math.Max(diag / 1500.0, 1e-3);

            // Build a clipper from electrode surfaces only. Streamlines should
            // pass through dielectrics (pressboard, paper, oil) and only terminate
            // on conductors (windings, static-ring metals, Dirichlet domain walls).
            GeometryClipper? clipper = null;
            var loops = new List<ClipLoop>();
            int nextId = 0;
            foreach (var surf in GeometryBuilder.ElectrodeSurfaces)
                loops.Add(new ClipLoop(nextId++, surf.Boundary, IsHole: true));
            foreach (var wallLoop in GeometryBuilder.DirichletWallLoops)
                loops.Add(new ClipLoop(nextId++, wallLoop, IsHole: false));
            if (loops.Count > 0)
                clipper = new GeometryClipper(loops);
            var optsFwd = new StreamlineTracerOptions
            {
                StepSize = step,
                MaxSteps = 5000,
                Direction = TraceDirection.Forward,
            };
            var optsBwd = new StreamlineTracerOptions
            {
                StepSize = step,
                MaxSteps = 5000,
                Direction = TraceDirection.Backward,
            };
            var tracerFwd = new StreamlineTracer(sampler, optsFwd, clipper);
            var tracerBwd = new StreamlineTracer(sampler, optsBwd, clipper);

            // Seed from electrode boundaries rather than a uniform volume grid: the
            // worst-case dielectric stress is a surface phenomenon (it peaks at conductor
            // surfaces and high-curvature fillets/corners), so a box grid over-samples
            // bulk oil and misses the lines that matter. All electrodes are treated
            // uniformly. The strategy is pluggable via IStreamlineSeedStrategy.
            var boundaries = new List<GeomLineLoop>();
            foreach (var surf in GeometryBuilder.ElectrodeSurfaces)
                boundaries.Add(surf.Boundary);
            boundaries.AddRange(GeometryBuilder.DirichletWallLoops);

            var seedContext = new StreamlineSeedContext
            {
                Sampler = sampler,
                Boundaries = boundaries,
                StepSize = step,
            };
            IStreamlineSeedStrategy seedStrategy = new ElectrodeBoundarySeedStrategy();

            foreach (var seed in seedStrategy.GenerateSeeds(seedContext))
            {
                {
                    double x = seed.X;
                    double y = seed.Y;

                    sampler.ResetHint();
                    var b = tracerBwd.Trace(x, y);
                    sampler.ResetHint();
                    var f = tracerFwd.Trace(x, y);

                    if (b.Points.Count + f.Points.Count < 2) continue;

                    // Only keep streamlines that terminate on an electrode at both ends.
                    // Lines that exit the meshed domain (top of window, axis, etc.) without
                    // hitting a conductor are usually fragments from poorly-placed seeds
                    // and clutter the plot.
                    bool bHit = b.TerminationReason == TraceTerminationReason.HitConductor;
                    bool fHit = f.TerminationReason == TraceTerminationReason.HitConductor;
                    if (!bHit || !fHit) continue;

                    // Both ends must land on *different* electrodes. A real field line runs
                    // from higher to lower potential, so ∫E·dl along it is strictly positive
                    // and it can never start and end on the same equipotential conductor.
                    // Lines that report the same surface at both ends are numerical artifacts:
                    // a seed pushed ~1.5·step off a surface whose forward and backward stubs
                    // both curve back into that same conductor in a low-field corner. They
                    // appear as spurious sub-mm lines (length ~ the seed offset) with a tiny
                    // drop, so drop them.
                    if (b.HitSurfaceId == f.HitSurfaceId) continue;

                    // Stitch backward-trace (reversed) + forward-trace into a single polyline.
                    var pts = new List<StreamlinePoint>(b.Points.Count + f.Points.Count);
                    for (int k = b.Points.Count - 1; k >= 0; k--) pts.Add(b.Points[k]);
                    for (int k = 1; k < f.Points.Count; k++) pts.Add(f.Points[k]);

                    lines.Add(StreamlineMarginCalculator.Compute(
                        new Streamline
                        {
                            Points = pts,
                            TotalLength = b.TotalLength + f.TotalLength,
                            TerminationReason = f.TerminationReason,
                        },
                        sol.PhysicalNames,
                        s_designCurves));
                }
            }

            // Worst-case streamlines drive design validation, but dense boundary
            // seeding (4x finer on fillets, see ElectrodeBoundarySeedStrategy) makes the
            // lowest-margin lines a redundant cluster hugging a single hot fillet, which
            // crowds other distinct critical gaps out of the displayed set. Rank by
            // safety margin (smallest = worst first), then apply spatial non-maximum
            // suppression on each line's governing hot-spot so the kept set covers
            // *distinct* critical regions rather than duplicating one corner. This 1-based
            // order is also the numbering used by the stress table and the Results-plot
            // hover tooltip.
            const int maxLines = 50;
            lines.Sort((a, b) => a.MinMargin.CompareTo(b.MinMargin));

            // Two kept hot-spots must be at least this far apart (domain-relative so it
            // adapts across cases). Tuned to collapse one fillet's worth of near-identical
            // lines while preserving genuinely separate stress regions.
            double suppressRadius = diag * 0.02;
            double suppress2 = suppressRadius * suppressRadius;

            var kept = new List<StreamlineWithMargin>(Math.Min(maxLines, lines.Count));
            var keptHotspots = new List<(double X, double Y)>(kept.Capacity);
            foreach (var line in lines)
            {
                if (kept.Count >= maxLines) break;
                var (hx, hy) = GoverningHotspot(line);
                bool tooClose = false;
                foreach (var (kx, ky) in keptHotspots)
                {
                    double hdx = hx - kx, hdy = hy - ky;
                    if (hdx * hdx + hdy * hdy < suppress2) { tooClose = true; break; }
                }
                if (tooClose) continue;
                kept.Add(line);
                keptHotspots.Add((hx, hy));
            }

            // If suppression left spare slots, backfill with the next-worst-margin lines
            // so the plot still shows up to maxLines (diversity first, then density).
            if (kept.Count < maxLines && kept.Count < lines.Count)
            {
                var keptSet = new HashSet<StreamlineWithMargin>(kept);
                foreach (var line in lines)
                {
                    if (kept.Count >= maxLines) break;
                    if (keptSet.Add(line)) kept.Add(line);
                }
            }

            // Renumber worst-first: the *set* is spatially diversified, but the table and
            // hover tooltip still expect #1 to be the lowest-margin line.
            kept.Sort((a, b) => a.MinMargin.CompareTo(b.MinMargin));
            return kept;
        }

        /// <summary>
        /// Representative point for a streamline's critical stress, used to spatially
        /// diversify the displayed set. Returns the location of the peak local |E| in the
        /// governing oil gap (the worst, minimum-margin gap, matching the stress table).
        /// Lines with no oil gap fall back to their geometric midpoint so the suppression
        /// still spreads these unconstrained lines apart.
        /// </summary>
        private static (double X, double Y) GoverningHotspot(StreamlineWithMargin line)
        {
            OilGapCumulativeMargin? governing = null;
            foreach (var g in line.OilGaps)
            {
                if (governing == null || g.GoverningMargin < governing.GoverningMargin)
                    governing = g;
            }
            if (governing != null)
                return (governing.MaxLocalEX, governing.MaxLocalEY);

            var pts = line.Points;
            if (pts.Count == 0) return (0, 0);
            var mid = pts[pts.Count / 2];
            return (mid.X, mid.Y);
        }

        private static readonly IDesignCurves s_designCurves = new DefaultWiedmannCurves();
    }
}
