using System.Collections.Generic;
using CommunityToolkit.Mvvm.ComponentModel;
using electrostat;
using GeometryLib;
using TfmrLib.FEM;

namespace electrostat_UI.ViewModels
{
    /// <summary>
    /// The solved result for one voltage permutation (scenario). Holds the per-scenario
    /// field solution and traced streamlines plus aggregate worst-case metrics, and doubles
    /// as a row in the cross-scenario envelope table (see <see cref="IsGoverning"/>).
    /// </summary>
    public partial class ScenarioResult : ObservableObject
    {
        /// <summary>Scenario display name (e.g. "HV impulse"), or "Base" for the single-solve case.</summary>
        public required string Name { get; init; }

        /// <summary>Field solution for this scenario, or null if the solve produced no results.</summary>
        public FEMSolution? Solution { get; init; }

        /// <summary>Geometry used for this scenario (identical shape across scenarios).</summary>
        public Geometry? Geometry { get; init; }

        /// <summary>Streamlines traced for this scenario (worst-margin first), or null.</summary>
        public IReadOnlyList<StreamlineWithMargin>? Streamlines { get; init; }

        /// <summary>Worst (minimum) cumulative safety margin across all streamlines; +∞ if unconstrained.</summary>
        public double WorstMargin { get; init; } = double.PositiveInfinity;

        /// <summary>Maximum local |E| anywhere in this scenario (kV/mm).</summary>
        public double MaxField { get; init; }

        /// <summary>1-based index of the governing (worst-margin) streamline, or 0 if none.</summary>
        public int GoverningIndex { get; init; }

        /// <summary>World X of the governing hot-spot.</summary>
        public double GoverningX { get; init; }

        /// <summary>World Y of the governing hot-spot.</summary>
        public double GoverningY { get; init; }

        /// <summary>Diagnostic detail when the solve produced no results (else null).</summary>
        public string? StatusDetail { get; init; }

        /// <summary>True for the scenario with the overall worst margin (set after all solves).</summary>
        [ObservableProperty] private bool _isGoverning;

        public string DisplayName => Name;

        public string MarginText =>
            double.IsPositiveInfinity(WorstMargin) ? "∞" : $"{WorstMargin:F2}×";

        public string GoverningLocation =>
            GoverningIndex > 0 ? $"#{GoverningIndex} ({GoverningX:F1}, {GoverningY:F1})" : "n/a";

        public int StreamlineCount => Streamlines?.Count ?? 0;
    }
}
