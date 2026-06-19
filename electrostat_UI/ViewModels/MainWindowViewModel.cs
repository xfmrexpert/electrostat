using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using electrostat;
using electrostat.IO;
using electrostat_UI.Services;
using electrostat_UI.ViewModels.Components;
using GeometryLib;
using TfmrLib.FEM;

namespace electrostat_UI.ViewModels
{
    public partial class MainWindowViewModel : ViewModelBase
    {
        public ObservableCollection<ExampleRef> Examples { get; } = new();

        public ObservableCollection<CaseTreeNode> CaseTree { get; } = new();

        [ObservableProperty]
        private Transformer? _selectedTransformer;

        [ObservableProperty]
        private TransformerViewModel? _editingTransformer;

        /// <summary>
        /// The Results workspace: cut-grouped solved results (per-cut and across all cuts),
        /// independent of the component tree / detail editor. Solve commands publish here.
        /// </summary>
        public ResultsWorkspaceViewModel Results { get; } = new();

        /// <summary>
        /// The cut whose geometry is previewed / solved. Driven by selecting a cut node in
        /// the explorer; defaults to the transformer's first cut.
        /// </summary>
        [ObservableProperty]
        private CutViewModel? _selectedCut;

        [ObservableProperty]
        private CaseTreeNode? _selectedTreeNode;

        [ObservableProperty]
        private object? _selectedComponent;

        [ObservableProperty]
        private IReadOnlyList<GeomSurface>? _highlightedSurfaces;

        [ObservableProperty]
        private Geometry? _currentGeometry;

        /// <summary>
        /// Per-surface semantic classification for the active geometry, letting the geometry
        /// view color each region by component type (winding, pressboard, static-ring paper,
        /// …) instead of a rotating index-based palette.
        /// </summary>
        [ObservableProperty]
        private IReadOnlyDictionary<GeomSurface, SurfaceCategory>? _surfaceCategories;

        [ObservableProperty]
        private string _statusMessage = "Select an example to render its geometry.";

        [ObservableProperty]
        private bool _isSolving;

        /// <summary>
        /// When true, the next solve requests adaptive mesh refinement (AMR) from the
        /// MFEM-ElectroMag solver: it refines the mesh around divergent fields (conductor
        /// corners) until its error/iteration/DOF criteria are met, improving |E| / stress
        /// accuracy. On by default now that the solver supports AMR; the Run-menu toggle
        /// can disable it to fall back to the single-solve path.
        /// </summary>
        [ObservableProperty]
        private bool _useAdaptiveRefinement = true;

        /// <summary>
        /// Full path of the file backing the current case, or null for an unsaved
        /// ("Untitled") document. Drives the window title and Save vs. Save As behavior.
        /// </summary>
        [ObservableProperty]
        [NotifyPropertyChangedFor(nameof(WindowTitle))]
        private string? _currentFilePath;

        /// <summary>True when the current case has unsaved edits.</summary>
        [ObservableProperty]
        [NotifyPropertyChangedFor(nameof(WindowTitle))]
        private bool _isModified;

        /// <summary>Document-style window title: "{file or Untitled}{* if modified} — electrostat".</summary>
        public string WindowTitle
        {
            get
            {
                string name = CurrentFilePath != null
                    ? Path.GetFileNameWithoutExtension(CurrentFilePath)
                    : "Untitled";
                return $"{name}{(IsModified ? "*" : "")} — electrostat";
            }
        }

        /// <summary>
        /// Active top-level workspace tab: 0 = Design (component tree + editor + geometry),
        /// 1 = Results. Bound to the root <c>TabControl.SelectedIndex</c>; auto-switches to
        /// Results after a successful solve so the freshly published results are shown.
        /// </summary>
        [ObservableProperty]
        private int _activeTabIndex;

        private const int DesignTabIndex = 0;
        private const int ResultsTabIndex = 1;

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

        // File pickers + "save changes?" prompt. Null at design time (parameterless ctor),
        // in which case the file commands are no-ops.
        private readonly IDialogService? _dialogService;

        // True while LoadTransformer is swapping the edited case in, so the resulting
        // Changed / StructureChanged notifications don't mark the fresh document modified.
        private bool _suppressDirty;

        /// <summary>Invoked by the Exit command to request the host window close.</summary>
        public Action? RequestClose { get; set; }

        public MainWindowViewModel() : this(null) { }

        public MainWindowViewModel(IDialogService? dialogService)
        {
            _dialogService = dialogService;

            LoadExamples();

            _rebuildTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(75) };
            _rebuildTimer.Tick += (_, _) => { _rebuildTimer.Stop(); RebuildGeometry(); };

            // Start on a fresh, unsaved document so the app never treats a bundled example
            // path as the working file.
            NewCase();
        }

        /// <summary>
        /// Discover the bundled <c>.estat</c> example files copied next to the executable and
        /// expose them (by display name) in the File ▸ Examples menu.
        /// </summary>
        private void LoadExamples()
        {
            Examples.Clear();
            string dir = Path.Combine(AppContext.BaseDirectory, "Examples");
            if (!Directory.Exists(dir)) return;

            foreach (var path in Directory.EnumerateFiles(dir, "*" + TransformerSerializer.FileExtension)
                                          .OrderBy(p => p, StringComparer.OrdinalIgnoreCase))
            {
                Examples.Add(new ExampleRef(Path.GetFileNameWithoutExtension(path), path));
            }
        }

        partial void OnSelectedTransformerChanged(Transformer? value)
        {
            Results.Clear();
            // A new case has no results yet, so return to the Design workspace.
            ActiveTabIndex = DesignTabIndex;

            if (EditingTransformer != null)
            {
                EditingTransformer.Changed -= OnCaseChanged;
                EditingTransformer.StructureChanged -= OnCaseStructureChanged;
            }

            EditingTransformer = value != null ? new TransformerViewModel(value) : null;

            if (EditingTransformer != null)
            {
                EditingTransformer.Changed += OnCaseChanged;
                EditingTransformer.StructureChanged += OnCaseStructureChanged;
            }

            // Default the active cut to the transformer's first cut so the preview / solve
            // commands have a target as soon as a transformer is selected.
            SelectedCut = EditingTransformer?.Cuts.FirstOrDefault();
            SolveCutCommand.NotifyCanExecuteChanged();
            SolveAllCutsCommand.NotifyCanExecuteChanged();

            RebuildCaseTree();
            RebuildGeometry();
        }

        partial void OnSelectedCutChanged(CutViewModel? value)
        {
            // Switching the active cut changes the domain / geometry type previewed, so
            // rebuild the geometry and refresh the solve commands' availability.
            SolveCutCommand.NotifyCanExecuteChanged();
            RebuildGeometry();
        }

        private void OnCaseChanged()
        {
            // Pure value edits (e.g. dragging a NumericUpDown) only affect the geometry.
            // Debounce: restart the timer on every change. The case explorer is left intact
            // so its expansion / selection survive editing — it is rebuilt only when the
            // structure actually changes (see OnCaseStructureChanged).
            if (!_suppressDirty) IsModified = true;
            _rebuildTimer.Stop();
            _rebuildTimer.Start();
        }

        private void OnCaseStructureChanged()
        {
            // Structural edits (add / remove / rename / re-parent) change tree labels or
            // membership, so refresh the explorer — preserving expansion + selection.
            if (!_suppressDirty) IsModified = true;
            RebuildCaseTree();
        }

        partial void OnSelectedTreeNodeChanged(CaseTreeNode? value)
        {
            // Ignore the transient null the TreeView reports while its items are swapped out
            // during a rebuild; RestoreSelection re-applies the real selection afterward.
            if (_suppressTreeSelectionSync && value == null)
                return;

            SelectedComponent = value?.Model;

            // Selecting a cut makes that cut the active one for preview / single-cut solve.
            // Its domain bounds are edited inline in the cut editor, so no separate node exists.
            if (value?.Model is CutViewModel cut)
                SelectedCut = cut;

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
            var tx = EditingTransformer;
            if (tx == null)
            {
                CurrentGeometry = null;
                HighlightedSurfaces = null;
                SurfaceCategories = null;
                StatusMessage = "No transformer loaded.";
                return;
            }

            // Preview the active cut (fall back to the first cut if none is selected yet).
            var cut = SelectedCut ?? tx.Cuts.FirstOrDefault();
            if (cut == null)
            {
                CurrentGeometry = null;
                HighlightedSurfaces = null;
                SurfaceCategories = null;
                StatusMessage = $"{tx.Name}: no cuts defined.";
                return;
            }

            try
            {
                var model = tx.ResolveCut(cut);
                _activeModel = GeometryBuilder.BuildGeometryOnly(model, clipToDomain: true);
                CurrentGeometry = _activeModel.Geometry;
                SurfaceCategories = _activeModel.SurfaceCategories;
                StatusMessage = $"{tx.Name} / {cut.Name} ({cut.GeometryType}): {CurrentGeometry.Surfaces.Count} surfaces";
                UpdateHighlight();
            }
            catch (Exception ex)
            {
                _activeModel = null;
                CurrentGeometry = null;
                HighlightedSurfaces = null;
                SurfaceCategories = null;
                StatusMessage = $"Failed to build {cut.Name}: {ex.Message}";
            }
        }

        // ---- File commands ----

        /// <summary>
        /// Replace the edited case with <paramref name="transformer"/>, recording its backing
        /// <paramref name="filePath"/> (null for an unsaved document) and clearing the dirty
        /// flag. Suppresses the change notifications raised while the new case is wired up.
        /// </summary>
        private void LoadTransformer(Transformer transformer, string? filePath)
        {
            _suppressDirty = true;
            try
            {
                SelectedTransformer = transformer;
            }
            finally
            {
                _suppressDirty = false;
            }

            CurrentFilePath = filePath;
            IsModified = false;
        }

        /// <summary>Start a fresh, unsaved case: empty components plus one default cut.</summary>
        private void NewCase()
        {
            var domain = new Domain(RInner: 100, ROuter: 500, ZLower: 0, ZUpper: 1500);
            var transformer = new Transformer(
                "Untitled",
                CoreLegRadius: 100,
                WindowWidth: 500,
                Windings: new List<WindingBlock>(),
                Pressboards: new List<PressboardBarrier>(),
                AngleRings: new List<AngleRing>(),
                StaticRings: new List<StaticRing>(),
                InterphaseBarriers: new List<PressboardBarrier>(),
                InterphaseAngleRings: new List<AngleRing>(),
                Voltages: new Dictionary<string, double>(),
                Scenarios: null,
                Cuts: new List<Cut> { new Cut("Cut_1", domain, GeometryType.Axisymmetric) });

            LoadTransformer(transformer, filePath: null);
        }

        /// <summary>
        /// If the current case has unsaved changes, ask whether to save / discard / cancel.
        /// Returns false only when the user cancels (so the caller should abort its action).
        /// </summary>
        public async Task<bool> PromptSaveIfDirtyAsync()
        {
            if (!IsModified || _dialogService == null)
                return true;

            var result = await _dialogService.ConfirmSaveChangesAsync();
            return result switch
            {
                SaveChangesResult.Save => await SaveCoreAsync(saveAs: false),
                SaveChangesResult.Discard => true,
                _ => false,
            };
        }

        private async Task LoadFromFileAsync(string path)
        {
            try
            {
                var transformer = await TransformerSerializer.LoadAsync(path);
                LoadTransformer(transformer, path);
                StatusMessage = $"Opened {Path.GetFileName(path)}.";
            }
            catch (Exception ex)
            {
                StatusMessage = $"Open failed: {ex.Message}";
            }
        }

        /// <summary>
        /// Serialize the current case. Prompts for a path when <paramref name="saveAs"/> is
        /// true or the case has no backing file yet. Returns true on a successful write.
        /// </summary>
        private async Task<bool> SaveCoreAsync(bool saveAs)
        {
            if (EditingTransformer == null || _dialogService == null)
                return false;

            string? path = CurrentFilePath;
            if (saveAs || path == null)
            {
                string suggested = EditingTransformer.Name + TransformerSerializer.FileExtension;
                path = await _dialogService.SaveFileAsync(suggested);
                if (path == null) return false;
            }

            try
            {
                await TransformerSerializer.SaveAsync(EditingTransformer.ToModel(), path);
                CurrentFilePath = path;
                IsModified = false;
                StatusMessage = $"Saved {Path.GetFileName(path)}.";
                return true;
            }
            catch (Exception ex)
            {
                StatusMessage = $"Save failed: {ex.Message}";
                return false;
            }
        }

        [RelayCommand]
        private async Task NewAsync()
        {
            if (!await PromptSaveIfDirtyAsync()) return;
            NewCase();
            StatusMessage = "New case.";
        }

        [RelayCommand]
        private async Task OpenAsync()
        {
            if (_dialogService == null) return;
            if (!await PromptSaveIfDirtyAsync()) return;

            var path = await _dialogService.OpenFileAsync();
            if (path == null) return;
            await LoadFromFileAsync(path);
        }

        [RelayCommand]
        private async Task OpenExampleAsync(ExampleRef? example)
        {
            if (example == null) return;
            if (!await PromptSaveIfDirtyAsync()) return;
            await LoadFromFileAsync(example.Path);
        }

        [RelayCommand]
        private async Task SaveAsync() => await SaveCoreAsync(saveAs: false);

        [RelayCommand]
        private async Task SaveAsAsync() => await SaveCoreAsync(saveAs: true);

        [RelayCommand]
        private void Exit() => RequestClose?.Invoke();

        // ---- Add / Remove component commands ----

        [RelayCommand]
        private void AddWinding()
        {
            if (EditingTransformer == null) return;
            EditingTransformer.Windings.Add(new WindingViewModel
            {
                Name = UniqueName("Winding", EditingTransformer.Windings.Select(x => x.Name)),
                R0 = 100, ZBottom = 0, Width = 50, Height = 200, FilletR = 1
            });
        }

        [RelayCommand]
        private void AddPressboard()
        {
            if (EditingTransformer == null) return;
            EditingTransformer.Pressboards.Add(new PressboardViewModel
            {
                Name = UniqueName("PB", EditingTransformer.Pressboards.Select(x => x.Name)),
                R0 = 100, ZBottom = 0, Thickness = 3, Height = 200
            });
        }

        [RelayCommand]
        private void AddAngleRing()
        {
            if (EditingTransformer == null) return;
            EditingTransformer.AngleRings.Add(new AngleRingViewModel
            {
                Name = UniqueName("AR", EditingTransformer.AngleRings.Select(x => x.Name)),
                R0 = 100, ZCorner = 0, Tv = 3, Hv = 50, Th = 3, Wh = 50, InsideFilletR = 5
            });
        }

        [RelayCommand]
        private void AddStaticRing()
        {
            if (EditingTransformer == null) return;
            EditingTransformer.StaticRings.Add(new StaticRingViewModel
            {
                Name = UniqueName("SR", EditingTransformer.StaticRings.Select(x => x.Name)),
                R0 = 100, ZBottom = 0, Width = 30, Height = 12, TPaper = 2
            });
        }

        [RelayCommand]
        private void AddScenario()
        {
            if (EditingTransformer == null) return;
            // New scenario seeds each winding's voltage from its current per-winding value
            // so it starts as a copy of present conditions, ready to edit.
            var sc = new VoltageScenarioViewModel(
                UniqueName("Scenario", EditingTransformer.Scenarios.Select(x => x.Name)));
            foreach (var wdg in EditingTransformer.Windings)
                sc.Cells.Add(new ScenarioVoltageCell(wdg.Name, wdg.Voltage));
            EditingTransformer.Scenarios.Add(sc);
        }

        [RelayCommand]
        private void AddCut()
        {
            if (EditingTransformer == null) return;
            // Seed a new cut from the currently selected cut's domain (or a sensible default),
            // so it starts as an editable copy near the existing geometry.
            var template = SelectedCut ?? EditingTransformer.Cuts.FirstOrDefault();
            var domain = template != null
                ? new Domain(template.Domain.RInner, template.Domain.ROuter,
                             template.Domain.ZLower, template.Domain.ZUpper)
                : new Domain(RInner: 100, ROuter: 500, ZLower: 0, ZUpper: 1500);

            var cut = new CutViewModel(new Cut(
                UniqueName("Cut", EditingTransformer.Cuts.Select(x => x.Name)),
                domain,
                template?.GeometryType ?? TfmrLib.FEM.GeometryType.Axisymmetric,
                template?.IncludeAdjacentPhase ?? false));
            EditingTransformer.Cuts.Add(cut);
            SelectedCut = cut;
        }

        [RelayCommand]
        private void RemoveSelected()
        {
            if (EditingTransformer == null) return;
            switch (SelectedComponent)
            {
                case WindingViewModel w: EditingTransformer.Windings.Remove(w); break;
                case PressboardViewModel p: EditingTransformer.Pressboards.Remove(p); break;
                case AngleRingViewModel a: EditingTransformer.AngleRings.Remove(a); break;
                case StaticRingViewModel s: EditingTransformer.StaticRings.Remove(s); break;
                case VoltageScenarioViewModel sc: EditingTransformer.Scenarios.Remove(sc); break;
                case CutViewModel cut:
                    EditingTransformer.Cuts.Remove(cut);
                    if (ReferenceEquals(SelectedCut, cut))
                        SelectedCut = EditingTransformer.Cuts.FirstOrDefault();
                    break;
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
                var tx = EditingTransformer;
                if (tx == null)
                {
                    _lastRootKey = null;
                    return;
                }

                var root = new CaseTreeNode(tx.Name, tx);

                // Nest each static ring under its parent winding; rings without a parent stay
                // in the top-level "Static Rings" group.
                var w = new CaseTreeNode($"Windings ({tx.Windings.Count})", key: GroupWindings);
                foreach (var x in tx.Windings)
                {
                    var wNode = new CaseTreeNode(x.Name, x);
                    foreach (var sr in tx.StaticRings)
                        if (sr.IsVoltageInherited && sr.ParentWinding == x.Name)
                            wNode.Children.Add(new CaseTreeNode($"{sr.Name} (ring)", sr));
                    w.Children.Add(wNode);
                }
                root.Children.Add(w);

                var p = new CaseTreeNode($"Pressboards ({tx.Pressboards.Count})", key: GroupPressboards);
                foreach (var x in tx.Pressboards) p.Children.Add(new CaseTreeNode(x.Name, x));
                root.Children.Add(p);

                var a = new CaseTreeNode($"Angle Rings ({tx.AngleRings.Count})", key: GroupAngleRings);
                foreach (var x in tx.AngleRings) a.Children.Add(new CaseTreeNode(x.Name, x));
                root.Children.Add(a);

                var unparented = tx.StaticRings.Where(sr => !sr.IsVoltageInherited).ToList();
                var s = new CaseTreeNode($"Static Rings ({unparented.Count})", key: GroupStaticRings);
                foreach (var x in unparented) s.Children.Add(new CaseTreeNode(x.Name, x));
                root.Children.Add(s);

                // Interphase insulation: shown only when present; mirrored automatically for
                // adjacent-phase cuts. Groups both interphase barriers and angle rings.
                int interphaseCount = tx.InterphaseBarriers.Count + tx.InterphaseAngleRings.Count;
                var ip = new CaseTreeNode($"Interphase ({interphaseCount})", key: GroupInterphase);
                foreach (var x in tx.InterphaseBarriers) ip.Children.Add(new CaseTreeNode(x.Name, x));
                foreach (var x in tx.InterphaseAngleRings) ip.Children.Add(new CaseTreeNode(x.Name, x));
                root.Children.Add(ip);

                var sc = new CaseTreeNode($"Scenarios ({tx.Scenarios.Count})", key: GroupScenarios);
                foreach (var x in tx.Scenarios) sc.Children.Add(new CaseTreeNode(x.Name, x));
                root.Children.Add(sc);

                // Cuts: each cut owns its own domain bounds, geometry type and phase mode.
                // The domain bounds are edited inline in the cut editor, so the cut is a leaf.
                var cutsGroup = new CaseTreeNode($"Cuts ({tx.Cuts.Count})", key: GroupCuts);
                foreach (var cut in tx.Cuts)
                    cutsGroup.Children.Add(new CaseTreeNode(cut.Name, cut));
                root.Children.Add(cutsGroup);

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
        private const string GroupInterphase = "group:interphase";
        private const string GroupScenarios = "group:scenarios";
        private const string GroupCuts = "group:cuts";

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

        private bool CanSolveCut() =>
            EditingTransformer != null && SelectedCut != null && !IsSolving;

        private bool CanSolveAllCuts() =>
            EditingTransformer != null && EditingTransformer.Cuts.Count > 0 && !IsSolving;

        /// <summary>
        /// Solve the currently selected cut on its own, replacing the results view with that
        /// cut's scenarios.
        /// </summary>
        [RelayCommand(CanExecute = nameof(CanSolveCut))]
        private async Task SolveCutAsync()
        {
            var tx = EditingTransformer;
            var cut = SelectedCut;
            if (tx == null || cut == null) return;

            IsSolving = true;
            SolveCutCommand.NotifyCanExecuteChanged();
            SolveAllCutsCommand.NotifyCanExecuteChanged();
            try
            {
                var scenarios = await SolveCaseAsync(tx.ResolveCut(cut), cut.Name);
                var cutResults = new List<CutResult> { CutResult.FromScenarios(cut.Name, scenarios) };
                Results.Publish(cutResults);
                ReportSolveStatus(cutResults, cut.Name);
                ActiveTabIndex = ResultsTabIndex;
            }
            catch (Exception exc)
            {
                StatusMessage = $"Solve failed: {exc.Message}";
            }
            finally
            {
                IsSolving = false;
                SolveCutCommand.NotifyCanExecuteChanged();
                SolveAllCutsCommand.NotifyCanExecuteChanged();
            }
        }

        /// <summary>
        /// Solve every cut of the current transformer in turn, publishing one
        /// <see cref="CutResult"/> per cut so results stay grouped by cut.
        /// </summary>
        [RelayCommand(CanExecute = nameof(CanSolveAllCuts))]
        private async Task SolveAllCutsAsync()
        {
            var tx = EditingTransformer;
            if (tx == null || tx.Cuts.Count == 0) return;

            IsSolving = true;
            SolveCutCommand.NotifyCanExecuteChanged();
            SolveAllCutsCommand.NotifyCanExecuteChanged();
            try
            {
                var cuts = tx.Cuts.ToList();
                var cutResults = new List<CutResult>(cuts.Count);
                for (int i = 0; i < cuts.Count; i++)
                {
                    var cut = cuts[i];
                    StatusMessage = $"Solving cut {i + 1}/{cuts.Count}: {cut.Name}...";
                    var scenarios = await SolveCaseAsync(tx.ResolveCut(cut), cut.Name);
                    cutResults.Add(CutResult.FromScenarios(cut.Name, scenarios));
                }
                Results.Publish(cutResults);
                ReportSolveStatus(cutResults, $"{tx.Name} ({cuts.Count} cuts)");
                ActiveTabIndex = ResultsTabIndex;
            }
            catch (Exception exc)
            {
                StatusMessage = $"Solve failed: {exc.Message}";
            }
            finally
            {
                IsSolving = false;
                SolveCutCommand.NotifyCanExecuteChanged();
                SolveAllCutsCommand.NotifyCanExecuteChanged();
            }
        }

        /// <summary>
        /// Build the mesh for <paramref name="ex"/> once, solve every scenario against it,
        /// and return one <see cref="ScenarioResult"/> per scenario. Does not touch the
        /// results view; callers wrap the scenarios into a <see cref="CutResult"/> and publish.
        /// Cut identity is carried by the owning <see cref="CutResult"/>, so scenario names
        /// are left unprefixed.
        /// </summary>
        private async Task<List<ScenarioResult>> SolveCaseAsync(
            ElectrostatCase ex, string label)
        {
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

            var caseName = SafeName(label);

            // Build geometry + mesh ONCE. The build also creates a voltage Scenario for
            // every permutation (or a single "Base"), so the solver can evaluate them all
            // against this one mesh. Seed the base effective voltages so a Dirichlet BC /
            // terminal is created for every electrode and wall.
            StatusMessage = $"Building mesh for {label}...";

            // Capture the AMR choice on the UI thread before handing off to the worker.
            // When enabled, the solver refines around divergent fields and returns the
            // final refined mesh through the usual results contract; when disabled (the
            // default) amr stays null and the build/solve path is byte-for-byte as before.
            var amr = UseAdaptiveRefinement ? new AmrSettings { Enabled = true } : null;
            var builtModel = await Task.Run(() => GeometryBuilder.BuildMesh(
                ex with { Voltages = ex.EffectiveVoltages(null) },
                caseName, lc: 5.0, clipToDomain: true, amr: amr));
            _activeModel = builtModel;

            // The electrode surfaces / Dirichlet walls and geometry are stable for the
            // life of this mesh (no further rebuilds), so capture the geometry once and
            // reuse it for every scenario's streamlines and result.
            var geom = builtModel.Geometry;

            // Solve once: the solver evaluates every scenario in the deck and writes one
            // results file per scenario, which BuiltModel.Solve collects into
            // ScenarioSolutions.
            StatusMessage = hasScenarios
                ? $"Solving {label}: {total} scenario(s)..."
                : $"Solving {label}...";
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

            return results;
        }

        /// <summary>
        /// Report a summary status line for a freshly published set of cut results. The
        /// results themselves are owned by <see cref="Results"/>; this only narrates the
        /// outcome (solved counts and the overall governing cut) for <paramref name="label"/>.
        /// </summary>
        private void ReportSolveStatus(IReadOnlyList<CutResult> cuts, string label)
        {
            int scenarioTotal = 0;
            int scenarioSolved = 0;
            CutResult? governing = null;
            string? firstStatusDetail = null;

            foreach (var c in cuts)
            {
                scenarioTotal += c.ScenarioCount;
                scenarioSolved += c.SolvedScenarioCount;
                if (c.IsGoverning) governing = c;
                foreach (var s in c.Scenarios)
                    firstStatusDetail ??= s.StatusDetail;
            }

            if (scenarioTotal == 0)
            {
                StatusMessage = $"Solve completed but produced no scenarios for {label}.";
            }
            else if (scenarioSolved == 0)
            {
                StatusMessage = $"Solve completed but no results were loaded for {label}. " +
                                firstStatusDetail;
            }
            else
            {
                StatusMessage = $"Solved {label}: {scenarioSolved}/{scenarioTotal} scenario(s) " +
                                $"across {cuts.Count} cut(s). " +
                                (governing != null
                                    ? $"Governing: {governing.CutName} / {governing.GoverningScenarioName} " +
                                      $"(margin {governing.MarginText}, max |E| {governing.MaxField:N1} kV/mm)."
                                    : "No constrained gaps found.");
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

        partial void OnIsSolvingChanged(bool value)
        {
            SolveCutCommand.NotifyCanExecuteChanged();
            SolveAllCutsCommand.NotifyCanExecuteChanged();
        }

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
