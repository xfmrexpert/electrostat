using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using electrostat;
using GeometryLib;
using TfmrLib.FEM;

namespace electrostat_UI.ViewModels
{
    public partial class MainWindowViewModel : ViewModelBase
    {
        public ObservableCollection<ElectrostatCase> Examples { get; }

        [ObservableProperty]
        private ElectrostatCase? _selectedExample;

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
        private bool _showResultsMesh = true;

        public MainWindowViewModel()
        {
            Examples = new ObservableCollection<ElectrostatCase>(electrostat.Examples.All());
            if (Examples.Count > 0)
            {
                SelectedExample = Examples[0];
            }
        }

        partial void OnSelectedExampleChanged(ElectrostatCase? value)
        {
            CurrentSolution = null;
            SolveCommand.NotifyCanExecuteChanged();

            if (value == null)
            {
                CurrentGeometry = null;
                StatusMessage = "No example selected.";
                return;
            }

            try
            {
                CurrentGeometry = GeometryBuilder.BuildGeometryOnly(
                    value, clipToDomain: true);
                StatusMessage = $"Loaded: {value.Name} ({CurrentGeometry.Surfaces.Count} surfaces)";
            }
            catch (Exception ex)
            {
                CurrentGeometry = null;
                StatusMessage = $"Failed to build {value.Name}: {ex.Message}";
            }
        }

        private bool CanSolve() => SelectedExample != null && !IsSolving;

        [RelayCommand(CanExecute = nameof(CanSolve))]
        private async Task SolveAsync()
        {
            var ex = SelectedExample;
            if (ex == null) return;

            IsSolving = true;
            SolveCommand.NotifyCanExecuteChanged();
            StatusMessage = $"Solving {ex.Name}...";

            try
            {
                var caseName = SafeName(ex.Name);
                var problem = await Task.Run(() =>
                    GeometryBuilder.BuildAndSolve(ex, caseName, lc: 5.0, clipToDomain: true));

                // Rebuild geometry view to match what we solved.
                CurrentGeometry = GeometryBuilder.geometry;

                CurrentSolution = problem?.Solution;
                if (CurrentSolution == null)
                {
                    var reason = (problem as MFEMProblem)?.LastLoadError;
                    var resultsPath = (problem as MFEMProblem)?.ResultsFile;
                    StatusMessage = $"Solve completed but no results were loaded for {ex.Name}. " +
                                    (reason ?? $"Expected results at '{resultsPath ?? "<unset>"}'.");
                }
                else
                {
                    StatusMessage = $"Solved {ex.Name}. Nodal views: {CurrentSolution.NodalScalars.Count}, " +
                                    $"elements: {CurrentSolution.Mesh?.Elements.Count ?? 0}.";
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
    }
}
