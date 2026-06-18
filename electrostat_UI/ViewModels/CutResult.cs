using System.Collections.Generic;
using CommunityToolkit.Mvvm.ComponentModel;

namespace electrostat_UI.ViewModels
{
    /// <summary>
    /// The solved results for one <see cref="electrostat.Cut"/>: every voltage permutation
    /// (scenario) evaluated against that cut's mesh, plus aggregate worst-case metrics rolled
    /// up across those scenarios. Doubles as a row in the across-all-cuts summary table (see
    /// <see cref="IsGoverning"/>) and as the selectable unit that drives the per-cut field
    /// plot, streamlines and stress table.
    /// </summary>
    public partial class CutResult : ObservableObject
    {
        /// <summary>Display name of the cut these results belong to (e.g. "Tank Cut - LV").</summary>
        public required string CutName { get; init; }

        /// <summary>Every scenario solved for this cut, in solve order.</summary>
        public required IReadOnlyList<ScenarioResult> Scenarios { get; init; }

        /// <summary>
        /// The cut's governing scenario: the one with a solution and the worst (minimum)
        /// safety margin. Null when no scenario produced a solution.
        /// </summary>
        public ScenarioResult? GoverningScenario { get; init; }

        /// <summary>Worst (minimum) cumulative safety margin across this cut's scenarios; +∞ if unconstrained.</summary>
        public double WorstMargin { get; init; } = double.PositiveInfinity;

        /// <summary>Maximum local |E| anywhere across this cut's scenarios (kV/mm).</summary>
        public double MaxField { get; init; }

        /// <summary>True for the cut with the overall worst margin across all cuts (set after all solves).</summary>
        [ObservableProperty] private bool _isGoverning;

        public string DisplayName => CutName;

        /// <summary>Number of scenarios solved for this cut.</summary>
        public int ScenarioCount => Scenarios.Count;

        /// <summary>Number of scenarios that produced a field solution.</summary>
        public int SolvedScenarioCount
        {
            get
            {
                int n = 0;
                foreach (var s in Scenarios)
                    if (s.Solution != null) n++;
                return n;
            }
        }

        /// <summary>Streamlines traced for the governing scenario (the worst-case design driver).</summary>
        public int StreamlineCount => GoverningScenario?.StreamlineCount ?? 0;

        public string MarginText =>
            double.IsPositiveInfinity(WorstMargin) ? "∞" : $"{WorstMargin:F2}×";

        /// <summary>Name of the governing scenario, or "n/a" when nothing solved.</summary>
        public string GoverningScenarioName => GoverningScenario?.Name ?? "n/a";

        /// <summary>Governing hot-spot (scenario + streamline + location), or "n/a".</summary>
        public string GoverningLocation =>
            GoverningScenario != null
                ? $"{GoverningScenario.Name}: {GoverningScenario.GoverningLocation}"
                : "n/a";

        /// <summary>
        /// Rolls up the scenarios solved for one cut into a <see cref="CutResult"/>: picks the
        /// governing (worst-margin) scenario among those that produced a solution and carries
        /// the worst margin / maximum field across all of them. Mirrors the governing-selection
        /// logic used for the cross-scenario envelope.
        /// </summary>
        public static CutResult FromScenarios(string cutName, IReadOnlyList<ScenarioResult> scenarios)
        {
            ScenarioResult? governing = null;
            double worstMargin = double.PositiveInfinity;
            double maxField = 0;

            foreach (var r in scenarios)
            {
                if (r.MaxField > maxField) maxField = r.MaxField;
                if (r.Solution == null) continue;
                if (governing == null || r.WorstMargin < governing.WorstMargin)
                    governing = r;
                if (r.WorstMargin < worstMargin) worstMargin = r.WorstMargin;
            }

            return new CutResult
            {
                CutName = cutName,
                Scenarios = scenarios,
                GoverningScenario = governing,
                WorstMargin = worstMargin,
                MaxField = maxField,
            };
        }
    }
}
