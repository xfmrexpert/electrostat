using CommunityToolkit.Mvvm.ComponentModel;
using electrostat;

namespace electrostat_UI.ViewModels.Components
{
    public partial class WindingViewModel : ComponentViewModel
    {
        [ObservableProperty] private double _r0;
        [ObservableProperty] private double _zBottom;
        [ObservableProperty] private double _width;
        [ObservableProperty] private double _height;
        [ObservableProperty] private double _filletR;
        [ObservableProperty] private double _voltage;

        public WindingViewModel() { Name = "Winding"; }

        public WindingViewModel(WindingBlock w, double voltage)
        {
            Name = w.Name;
            _r0 = w.R0;
            _zBottom = w.ZBottom;
            _width = w.Width;
            _height = w.Height;
            _filletR = w.FilletR;
            _voltage = voltage;
        }

        public WindingBlock ToModel() => new(Name, R0, ZBottom, Width, Height, FilletR);
    }
}
