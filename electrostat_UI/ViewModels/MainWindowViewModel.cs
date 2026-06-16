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

        /// <summary>
        /// One entry per solved voltage permutation (scenario). Drives the scenario
        /// selector and the cross-scenario envelope table. Empty until a solve runs.
        /// </summary>
        public ObservableCollection<ScenarioResult> ScenarioResults { get; } = new();

        /// <summary>
        /// The scenario whose field plot + stress table are currently shown. Selecting a
        /// different scenario swaps the displayed solution / streamlines / summary without
        /// re-solving.
        /// </summary>
        [ObservableProperty]
        private ScenarioResult? _selectedScenarioResult;

        // Coalesces rapid edits (e.g. while dragging a NumericUpDown spinner) into a
        // single geometry rebuild so the UI stays responsive.
        private readonly DispatcherTimer _rebuildTimer;

        // True while RebuildCaseTree is swapping the tree's nodes. The TreeView writes its
        // SelectedItem back to null as the old nodes are cleared; this guard stops that
        // transient null from tearing down the editor pane mid-rebuild.
        private bool _suppressTreeSelectionSync;

        // Identity of the root node from the previous rebuild. When it changes a different
        // case was loaded (so the explorer starts fresh with the root expanded); when it is
        // unchanged we are rebuilding the same case in place and preserve expansion exactly.
        private object? _lastRootKey;

        // The most recently built model (geometry-only rebuild or full mesh build). Owns the
        // component-surface lookup, electrode surfaces, and Dirichlet wall loops that the
        // highlight and streamline features read. Replaces the former GeometryBuilder statics.
        private BuiltModel? _activeModel;

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
            ScenarioResults.Clear();
            SelectedScenarioResult = null;
            SolveCommand.NotifyCanExecuteChanged();

            if (EditingCase != null)
            {
                EditingCase.Changed -= OnCaseChanged;
                EditingCase.StructureChanged -= OnCaseStructureChanged;
            }

            EditingCase = value != null ? new ElectrostatCaseViewModel(value) : null;

            if (EditingCase != null)
            {
                EditingCase.Changed += OnCaseChanged;
                EditingCase.StructureChanged += OnCaseStructureChanged;
            }

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

        partial void OnSelectedScenarioResultChanged(ScenarioResult? value)
        {
            // Swap the displayed field solution + streamlines to the chosen scenario without
            // re-solving. OnStreamlinesChanged rebuilds the stress summary table.
            if (value == null) return;
            CurrentSolution = value.Solution;
            CurrentGeometry = value.Geometry ?? CurrentGeometry;
            Streamlines = value.Streamlines;
            UpdateHighlight();
        }

        private void OnCaseChanged()
        {
            // Pure value edits (e.g. dragging a NumericUpDown) only affect the geometry.
            // Debounce: restart the timer on every change. The case explorer is left intact
            // so its expansion / selection survive editing — it is rebuilt only when the
            // structure actually changes (see OnCaseStructureChanged).
            _rebuildTimer.Stop();
            _rebuildTimer.Start();
        }

        private void OnCaseStructureChanged()
        {
            // Structural edits (add / remove / rename / re-parent) change tree labels or
            // membership, so refresh the explorer — preserving expansion + selection.
            RebuildCaseTree();
        }

        partial void OnSelectedTreeNodeChanged(CaseTreeNode? value)
        {
            // Ignore the transient null the TreeView reports while its items are swapped out
            // during a rebuild; RestoreSelection re-applies the real selection afterward.
            if (_suppressTreeSelectionSync && value == null)
                return;

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
                _activeModel == null ||
                !_activeModel.ComponentSurfaces.TryGetValue(key, out var list))
            {
                HighlightedSurfaces = null;
                return;
            }

            // Filter to surfaces still present in the current geometry (some may have
            // been removed after clipping). Electrode interiors (windings, static-ring
            // metals) are intentionally pruned from geometry.Surfaces so gmsh does not
            // mesh them, but they are still valid, drawable surfaces (their boundaries are
            // clipped in place) — include them so selected electrodes still highlight.
            var current = CurrentGeometry;
            if (current == null)
            {
                HighlightedSurfaces = list;
                return;
            }
            var live = new HashSet<GeomSurface>(current.Surfaces, ReferenceEqualityComparer.Instance);
            live.UnionWith(_activeModel.ElectrodeSurfaces);
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
                _activeModel = GeometryBuilder.BuildGeometryOnly(model, clipToDomain: true);
                CurrentGeometry = _activeModel.Geometry;
                StatusMessage = $"{model.Name}: {CurrentGeometry.Surfaces.Count} surfaces";
                UpdateHighlight();
            }
            catch (Exception ex)
            {
                _activeModel = null;
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
        private void AddScenario()
        {
            if (EditingCase == null) return;
            // New scenario seeds each winding's voltage from its current per-winding value
            // so it starts as a copy of present conditions, ready to edit.
            var sc = new VoltageScenarioViewModel(
                UniqueName("Scenario", EditingCase.Scenarios.Select(x => x.Name)));
            foreach (var wdg in EditingCase.Windings)
                sc.Cells.Add(new ScenarioVoltageCell(wdg.Name, wdg.Voltage));
            EditingCase.Scenarios.Add(sc);
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
                case VoltageScenarioViewModel sc: EditingCase.Scenarios.Remove(sc); break;
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
            // Preserve which nodes are expanded and which is selected across the rebuild so
            // editing a component does not collapse the explorer or clear the editor pane.
            var expandedKeys = new HashSet<object>(ReferenceEqualityComparer.Instance);
            CollectExpandedKeys(CaseTree, expandedKeys);
            var selectedKey = SelectedTreeNode?.Key;
            var previousRootKey = _lastRootKey;

            _suppressTreeSelectionSync = true;
            try
            {
                CaseTree.Clear();
                var ec = EditingCase;
                if (ec == null)
                {
                    _lastRootKey = null;
                    return;
                }

                var root = new CaseTreeNode(ec.Name, ec);

                root.Children.Add(new CaseTreeNode("Domain", ec.Domain));

                // Nest each static ring under its parent winding; rings without a parent stay
                // in the top-level "Static Rings" group.
                var w = new CaseTreeNode($"Windings ({ec.Windings.Count})", key: GroupWindings);
                foreach (var x in ec.Windings)
                {
                    var wNode = new CaseTreeNode(x.Name, x);
                    foreach (var sr in ec.StaticRings)
                        if (sr.IsVoltageInherited && sr.ParentWinding == x.Name)
                            wNode.Children.Add(new CaseTreeNode($"{sr.Name} (ring)", sr));
                    w.Children.Add(wNode);
                }
                root.Children.Add(w);

                var p = new CaseTreeNode($"Pressboards ({ec.Pressboards.Count})", key: GroupPressboards);
                foreach (var x in ec.Pressboards) p.Children.Add(new CaseTreeNode(x.Name, x));
                root.Children.Add(p);

                var a = new CaseTreeNode($"Angle Rings ({ec.AngleRings.Count})", key: GroupAngleRings);
                foreach (var x in ec.AngleRings) a.Children.Add(new CaseTreeNode(x.Name, x));
                root.Children.Add(a);

                var unparented = ec.StaticRings.Where(sr => !sr.IsVoltageInherited).ToList();
                var s = new CaseTreeNode($"Static Rings ({unparented.Count})", key: GroupStaticRings);
                foreach (var x in unparented) s.Children.Add(new CaseTreeNode(x.Name, x));
                root.Children.Add(s);

                var sc = new CaseTreeNode($"Scenarios ({ec.Scenarios.Count})", key: GroupScenarios);
                foreach (var x in ec.Scenarios) sc.Children.Add(new CaseTreeNode(x.Name, x));
                root.Children.Add(sc);

                CaseTree.Add(root);
                _lastRootKey = root.Key;

                // A different case was loaded: start fresh with every node expanded so the
                // whole component tree is visible by default. Rebuilding the same case in
                // place instead restores exactly what the user had expanded.
                if (!ReferenceEquals(previousRootKey, root.Key))
                    ExpandAll(CaseTree);

                RestoreExpansion(CaseTree, expandedKeys);

                // Re-select the same node; if it no longer exists (e.g. it was just removed),
                // clear the selection so the editor pane doesn't linger on a deleted item.
                if (!RestoreSelection(CaseTree, selectedKey))
                {
                    SelectedTreeNode = null;
                    SelectedComponent = null;
                    UpdateHighlight();
                }
            }
            finally
            {
                _suppressTreeSelectionSync = false;
            }
        }

        /// <summary>Group-node keys: stable across rebuilds even as the "(count)" label changes.</summary>
        private const string GroupWindings = "group:windings";
        private const string GroupPressboards = "group:pressboards";
        private const string GroupAngleRings = "group:anglerings";
        private const string GroupStaticRings = "group:staticrings";
        private const string GroupScenarios = "group:scenarios";

        private static void CollectExpandedKeys(IEnumerable<CaseTreeNode> nodes, HashSet<object> into)
        {
            foreach (var n in nodes)
            {
                if (n.IsExpanded) into.Add(n.Key);
                CollectExpandedKeys(n.Children, into);
            }
        }

        private static void ExpandAll(IEnumerable<CaseTreeNode> nodes)
        {
            foreach (var n in nodes)
            {
                n.IsExpanded = true;
                ExpandAll(n.Children);
            }
        }

        private static void RestoreExpansion(IEnumerable<CaseTreeNode> nodes, HashSet<object> expandedKeys)
        {
            foreach (var n in nodes)
            {
                if (expandedKeys.Contains(n.Key)) n.IsExpanded = true;
                RestoreExpansion(n.Children, expandedKeys);
            }
        }

        private bool RestoreSelection(IEnumerable<CaseTreeNode> nodes, object? selectedKey)
        {
            if (selectedKey == null) return false;
            foreach (var n in nodes)
            {
                if (ReferenceEquals(n.Key, selectedKey))
                {
                    SelectedTreeNode = n;
                    return true;
                }
                if (RestoreSelection(n.Children, selectedKey))
                    return true;
            }
            return false;
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

            // A case with no scenarios solves once from its base voltages (labelled "Base");
            // otherwise every scenario is evaluated. The geometry/mesh depends only on the
            // components (not the voltages), so it is built ONCE below and the solver
            // evaluates every scenario against that same mesh in a single run — only the
            // Dirichlet potentials change between permutations.
            var scenarios = ex.Scenarios is { Count: > 0 }
                ? ex.Scenarios
                : new List<VoltageScenario> { };

            bool hasScenarios = scenarios.Count > 0;
            int total = hasScenarios ? scenarios.Count : 1;

            try
            {
                var caseName = SafeName(ex.Name);

                // Build geometry + mesh ONCE. The build also creates a voltage Scenario for
                // every permutation (or a single "Base"), so the solver can evaluate them all
                // against this one mesh. Seed the base effective voltages so a Dirichlet BC /
                // terminal is created for every electrode and wall.
                StatusMessage = $"Building mesh for {ex.Name}...";
                var builtModel = await Task.Run(() => GeometryBuilder.BuildMesh(
                    ex with { Voltages = ex.EffectiveVoltages(null) },
                    caseName, lc: 5.0, clipToDomain: true));
                _activeModel = builtModel;

                // The electrode surfaces / Dirichlet walls and geometry are stable for the
                // life of this mesh (no further rebuilds), so capture the geometry once and
                // reuse it for every scenario's streamlines and result.
                var geom = builtModel.Geometry;

                // Solve once: the solver evaluates every scenario in the deck and writes one
                // results file per scenario, which BuiltModel.Solve collects into
                // ScenarioSolutions.
                StatusMessage = hasScenarios
                    ? $"Solving {ex.Name}: {total} scenario(s)..."
                    : $"Solving {ex.Name}...";
                await Task.Run(() => builtModel.Solve());

                var results = new List<ScenarioResult>(builtModel.ScenarioSolutions.Count);
                foreach (var scenarioSolution in builtModel.ScenarioSolutions)
                {
                    string scenarioName = scenarioSolution.Name;
                    var sol = scenarioSolution.Solution;

                    if (sol == null)
                    {
                        var reason = (builtModel.Problem as MFEMProblem)?.LastLoadError;
                        results.Add(new ScenarioResult
                        {
                            Name = scenarioName,
                            Geometry = geom,
                            StatusDetail = reason ?? $"No results were produced for scenario '{scenarioName}'.",
                        });
                        continue;
                    }

                    // Trace streamlines for this scenario's solution. The electrode surfaces
                    // / Dirichlet walls are shared by every scenario (same mesh), so only the
                    // field (sol) differs between permutations.
                    var streamlines = await Task.Run(() => BuildStreamlines(sol, geom, builtModel));
                    results.Add(BuildScenarioResult(scenarioName, sol, geom, streamlines));
                }

                // Flag the governing (overall worst-margin) scenario for the envelope view.
                ScenarioResult? governing = null;
                foreach (var r in results)
                {
                    r.IsGoverning = false;
                    if (r.Solution == null) continue;
                    if (governing == null || r.WorstMargin < governing.WorstMargin)
                        governing = r;
                }
                if (governing != null) governing.IsGoverning = true;

                ScenarioResults.Clear();
                foreach (var r in results) ScenarioResults.Add(r);

                // Show the governing scenario by default (worst case drives design review);
                // fall back to the first scenario that produced a solution, else the first.
                SelectedScenarioResult =
                    governing
                    ?? results.Find(r => r.Solution != null)
                    ?? (results.Count > 0 ? results[0] : null);

                int solved = results.FindAll(r => r.Solution != null).Count;
                if (solved == 0)
                {
                    StatusMessage = $"Solve completed but no results were loaded for {ex.Name}. " +
                                    (results.Count > 0 ? results[0].StatusDetail : null);
                }
                else if (hasScenarios)
                {
                    StatusMessage = $"Solved {ex.Name}: {solved}/{total} scenarios. " +
                                    (governing != null
                                        ? $"Governing: {governing.Name} (margin {governing.MarginText}, max |E| {governing.MaxField:N1} kV/mm)."
                                        : "No constrained gaps found.");
                }
                else
                {
                    var only = SelectedScenarioResult;
                    StatusMessage = $"Solved {ex.Name}. Nodal views: {only?.Solution?.NodalScalars.Count ?? 0}, " +
                                    $"elements: {only?.Solution?.Mesh?.Elements.Count ?? 0}, " +
                                    $"streamlines: {only?.StreamlineCount ?? 0}.";
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

        /// <summary>
        /// Aggregate a scenario's traced streamlines into a <see cref="ScenarioResult"/>:
        /// worst (minimum) safety margin, maximum local |E|, and the governing hot-spot.
        /// The streamline list arrives sorted worst-margin first.
        /// </summary>
        private static ScenarioResult BuildScenarioResult(
            string name, FEMSolution sol, Geometry? geom,
            IReadOnlyList<StreamlineWithMargin> streamlines)
        {
            double worstMargin = double.PositiveInfinity;
            double maxField = 0;
            int governingIndex = 0;
            double gx = 0, gy = 0;

            for (int i = 0; i < streamlines.Count; i++)
            {
                var s = streamlines[i];
                if (s.Stress.MaxE > maxField) maxField = s.Stress.MaxE;
                if (s.MinMargin < worstMargin)
                {
                    worstMargin = s.MinMargin;
                    governingIndex = i + 1;
                    (gx, gy) = GoverningHotspot(s);
                }
            }

            return new ScenarioResult
            {
                Name = name,
                Solution = sol,
                Geometry = geom,
                Streamlines = streamlines,
                WorstMargin = worstMargin,
                MaxField = maxField,
                GoverningIndex = governingIndex,
                GoverningX = gx,
                GoverningY = gy,
            };
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
        private static IReadOnlyList<StreamlineWithMargin> BuildStreamlines(FEMSolution sol, Geometry? geom, BuiltModel model)
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
            foreach (var surf in model.ElectrodeSurfaces)
                loops.Add(new ClipLoop(nextId++, surf.Boundary, IsHole: true));
            foreach (var wallLoop in model.DirichletWallLoops)
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
            foreach (var surf in model.ElectrodeSurfaces)
                boundaries.Add(surf.Boundary);
            boundaries.AddRange(model.DirichletWallLoops);

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
