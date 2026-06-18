using System.ComponentModel;
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

        /// <summary>Editable taper on the vertical-leg tip; disabled means a square end.</summary>
        public TaperViewModel TaperVTip { get; }

        public AngleRingViewModel() : this(default(AngleRing))
        {
            Name = "AngleRing";
        }

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
            TaperVTip = new TaperViewModel(ar.TaperVTip);

            // Surface nested taper edits as a change on this component so the owning
            // TransformerViewModel rebuilds the geometry (a non-Name change avoids a
            // structural tree refresh).
            TaperVTip.PropertyChanged += OnTaperChanged;
        }

        private void OnTaperChanged(object? sender, PropertyChangedEventArgs e) =>
            OnPropertyChanged(nameof(TaperVTip));

        public AngleRing ToModel() =>
            new(Name, R0, ZCorner, Tv, Hv, Th, Wh, InsideFilletR, TaperVTip.ToModel());
    }
}
