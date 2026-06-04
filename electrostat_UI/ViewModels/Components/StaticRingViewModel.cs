using CommunityToolkit.Mvvm.ComponentModel;
using electrostat;

namespace electrostat_UI.ViewModels.Components
{
    public partial class StaticRingViewModel : ComponentViewModel
    {
        [ObservableProperty] private double _r0;
        [ObservableProperty] private double _zBottom;
        [ObservableProperty] private double _width;
        [ObservableProperty] private double _height;
        [ObservableProperty] private double _rTL;
        [ObservableProperty] private double _rTR;
        [ObservableProperty] private double _rBR;
        [ObservableProperty] private double _rBL;
        [ObservableProperty] private double _tPaper;
        [ObservableProperty] private double _voltage;

        public StaticRingViewModel() { Name = "StaticRing"; }

        public StaticRingViewModel(StaticRing sr, double voltage)
        {
            Name = sr.Name;
            _r0 = sr.R0;
            _zBottom = sr.ZBottom;
            _width = sr.Width;
            _height = sr.Height;
            _rTL = sr.RTL;
            _rTR = sr.RTR;
            _rBR = sr.RBR;
            _rBL = sr.RBL;
            _tPaper = sr.TPaper;
            _voltage = voltage;
        }

        public StaticRing ToModel() =>
            new(Name, R0, ZBottom, Width, Height, RTL, RTR, RBR, RBL, TPaper);
    }
}
