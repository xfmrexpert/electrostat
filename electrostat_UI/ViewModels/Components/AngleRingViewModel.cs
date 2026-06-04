using CommunityToolkit.Mvvm.ComponentModel;
using electrostat;

namespace electrostat_UI.ViewModels.Components
{
    public partial class AngleRingViewModel : ComponentViewModel
    {
        [ObservableProperty] private double _r0;
        [ObservableProperty] private double _zCorner;
        [ObservableProperty] private double _tv;
        [ObservableProperty] private double _hv;
        [ObservableProperty] private double _th;
        [ObservableProperty] private double _wh;
        [ObservableProperty] private double _insideFilletR;

        public Taper? TaperVTip { get; set; }

        public AngleRingViewModel() { Name = "AngleRing"; }

        public AngleRingViewModel(AngleRing ar)
        {
            Name = ar.Name;
            _r0 = ar.R0;
            _zCorner = ar.ZCorner;
            _tv = ar.Tv;
            _hv = ar.Hv;
            _th = ar.Th;
            _wh = ar.Wh;
            _insideFilletR = ar.InsideFilletR;
            TaperVTip = ar.TaperVTip;
        }

        public AngleRing ToModel() =>
            new(Name, R0, ZCorner, Tv, Hv, Th, Wh, InsideFilletR, TaperVTip);
    }
}
