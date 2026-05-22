using System.Collections.Generic;
using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using electrostat;
using GeometryLib;

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
        private string _statusMessage = "Select an example to render its geometry.";

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
            catch (System.Exception ex)
            {
                CurrentGeometry = null;
                StatusMessage = $"Failed to build {value.Name}: {ex.Message}";
            }
        }
    }
}
