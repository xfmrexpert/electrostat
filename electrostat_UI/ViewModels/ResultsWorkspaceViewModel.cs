using System.Collections.Generic;
using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using electrostat;
using GeometryLib;
using TfmrLib.FEM;

namespace electrostat_UI.ViewModels
{
    /// <summary>
    /// Hosts the Results workspace: a summary of solved results across all cuts, a per-cut
    /// drill-down (scenario selector + field plot + streamlines + streamline-stress table),
    /// and a per-streamline detail. Independent of the component tree / detail editor; the
    /// owning <see cref="MainWindowViewModel"/> publishes solved <see cref="CutResult"/>s here.
    /// </summary>
    /// <remarks>
    /// Selection cascades top-down: choosing a <see cref="CutResult"/> repopulates
    /// <see cref="ScenarioResults"/> and selects that cut's governing scenario; choosing a
    /// <see cref="ScenarioResult"/> swaps the field plot's solution / geometry / streamlines
    /// (this VM keeps its OWN geometry so it never disturbs the Design-mode live preview).
    /// </remarks>
    public partial class ResultsWorkspaceViewModel : ViewModelBase
    {
        // ---- Across-all-cuts summary ----

        /// <summary>One entry per solved cut. Drives the all-cuts summary table and cut selector.</summary>
        public ObservableCollection<CutResult> CutResults { get; } = new();

        /// <summary>The cut whose scenarios / plots are currently shown. Selecting it cascades down.</summary>
        [ObservableProperty] private CutResult? _selectedCutResult;

        // ---- Per-cut scenarios ----

        /// <summary>Scenarios of <see cref="SelectedCutResult"/>, mirrored for the scenario selector.</summary>
        public ObservableCollection<ScenarioResult> ScenarioResults { get; } = new();

        /// <summary>The scenario whose field plot + stress table are shown for the selected cut.</summary>
        [ObservableProperty] private ScenarioResult? _selectedScenarioResult;

        // ---- Field plot ----

        [ObservableProperty] private FEMSolution? _currentSolution;
        [ObservableProperty] private Geometry? _currentGeometry;
        [ObservableProperty] private IReadOnlyList<StreamlineWithMargin>? _streamlines;

        public string[] AvailableFields { get; } = new[] { "V", "|E|" };
        [ObservableProperty] private string _selectedField = "V";
        [ObservableProperty] private bool _showResultsMesh = false;
        [ObservableProperty] private bool _showResultsOutlines = true;
        [ObservableProperty] private bool _showStreamlines = true;

        // ---- Per-streamline stress ----

        /// <summary>Per-streamline stress metrics for the selected scenario.</summary>
        public ObservableCollection<StreamlineSummaryRow> StreamlineSummary { get; } = new();

        // ---- Per-streamline detail ----

        /// <summary>
        /// The streamline row selected in the stress table. Drives the per-streamline detail
        /// plot below the table.
        /// </summary>
        [ObservableProperty] private StreamlineSummaryRow? _selectedStreamlineRow;

        /// <summary>
        /// The <see cref="StreamlineWithMargin"/> backing <see cref="SelectedStreamlineRow"/>,
        /// resolved by its 1-based index into the worst-first <see cref="Streamlines"/> list.
        /// Null when no row is selected. Fed to <c>StreamlineStressPlotView.Streamline</c>.
        /// </summary>
        public StreamlineWithMargin? SelectedStreamline { get; private set; }

        /// <summary>1-based number of <see cref="SelectedStreamline"/> (0 when none).</summary>
        public int SelectedStreamlineNumber => SelectedStreamlineRow?.Index ?? 0;

        /// <summary>True when a streamline row is selected so the detail plot can be shown.</summary>
        public bool HasSelectedStreamline => SelectedStreamline != null;

        /// <summary>True once at least one cut has been solved and published.</summary>
        public bool HasResults => CutResults.Count > 0;

        /// <summary>
        /// Replace the workspace with a freshly solved set of cuts: flag the governing
        /// (overall worst-margin) cut, repopulate the all-cuts summary, and select the
        /// governing cut by default so the worst case is shown first.
        /// </summary>
        public void Publish(IReadOnlyList<CutResult> cuts)
        {
            CutResult? governing = null;
            foreach (var c in cuts)
            {
                c.IsGoverning = false;
                if (c.GoverningScenario == null) continue;
                if (governing == null || c.WorstMargin < governing.WorstMargin)
                    governing = c;
            }
            if (governing != null) governing.IsGoverning = true;

            CutResults.Clear();
            foreach (var c in cuts) CutResults.Add(c);

            // Show the governing cut by default (worst case drives design review); fall back
            // to the first cut that produced a solution, else the first, else clear.
            SelectedCutResult =
                governing
                ?? FirstSolvedCut(cuts)
                ?? (CutResults.Count > 0 ? CutResults[0] : null);

            OnPropertyChanged(nameof(HasResults));
        }

        /// <summary>Clear all published results (e.g. when a new transformer is loaded).</summary>
        public void Clear()
        {
            CutResults.Clear();
            SelectedCutResult = null;
            OnPropertyChanged(nameof(HasResults));
        }

        partial void OnSelectedCutResultChanged(CutResult? value)
        {
            ScenarioResults.Clear();
            if (value == null)
            {
                SelectedScenarioResult = null;
                return;
            }

            foreach (var s in value.Scenarios) ScenarioResults.Add(s);

            // Default to the cut's governing scenario (worst case drives design review);
            // fall back to the first scenario that produced a solution, else the first.
            SelectedScenarioResult =
                value.GoverningScenario
                ?? FirstSolved(value.Scenarios)
                ?? (ScenarioResults.Count > 0 ? ScenarioResults[0] : null);
        }

        partial void OnSelectedScenarioResultChanged(ScenarioResult? value)
        {
            // Swap the displayed field solution + streamlines to the chosen scenario without
            // re-solving. OnStreamlinesChanged rebuilds the stress summary table. The geometry
            // comes from the scenario itself, so this NEVER touches the Design-mode preview.
            if (value == null)
            {
                CurrentSolution = null;
                CurrentGeometry = null;
                Streamlines = null;
                return;
            }

            CurrentSolution = value.Solution;
            CurrentGeometry = value.Geometry;
            Streamlines = value.Streamlines;
        }

        partial void OnStreamlinesChanged(IReadOnlyList<StreamlineWithMargin>? value)
        {
            StreamlineSummary.Clear();
            // Dropping the streamlines invalidates any selected row / detail plot.
            SelectedStreamlineRow = null;
            if (value == null) return;

            // The list arrives pre-sorted worst-first by minimum safety margin, so number
            // rows by list position. This 1-based index matches the Results-plot hover
            // tooltip, keeping the two views in sync.
            int index = 1;
            foreach (var s in value)
                StreamlineSummary.Add(StreamlineSummaryRow.FromStreamline(index++, s));
        }

        partial void OnSelectedStreamlineRowChanged(StreamlineSummaryRow? value)
        {
            // Resolve the row back to its streamline by 1-based position into the worst-first
            // list (the same ordering used to number the rows in OnStreamlinesChanged).
            var lines = Streamlines;
            SelectedStreamline =
                value != null && lines != null &&
                value.Index >= 1 && value.Index <= lines.Count
                    ? lines[value.Index - 1]
                    : null;

            OnPropertyChanged(nameof(SelectedStreamline));
            OnPropertyChanged(nameof(SelectedStreamlineNumber));
            OnPropertyChanged(nameof(HasSelectedStreamline));
        }

        private static ScenarioResult? FirstSolved(IReadOnlyList<ScenarioResult> scenarios)
        {
            foreach (var s in scenarios)
                if (s.Solution != null) return s;
            return null;
        }

        private static CutResult? FirstSolvedCut(IReadOnlyList<CutResult> cuts)
        {
            foreach (var c in cuts)
                if (c.GoverningScenario != null) return c;
            return null;
        }
    }
}
