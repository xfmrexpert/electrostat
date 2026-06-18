using System.ComponentModel;
using CommunityToolkit.Mvvm.ComponentModel;
using electrostat;

namespace electrostat_UI.ViewModels.Components
{
    public partial class PressboardViewModel : ComponentViewModel
    {
        [ObservableProperty] private double _r0;
        [ObservableProperty] private double _zBottom;
        [ObservableProperty] private double _thickness;
        [ObservableProperty] private double _height;

        /// <summary>Editable taper on the top (high-z) end; disabled means a square end.</summary>
        public TaperViewModel TaperTop { get; }

        /// <summary>Editable taper on the bottom (low-z) end; disabled means a square end.</summary>
        public TaperViewModel TaperBottom { get; }

        public PressboardViewModel() : this(default(PressboardBarrier))
        {
            Name = "Pressboard";
        }

        public PressboardViewModel(PressboardBarrier pb)
        {
            Name = pb.Name;
            _r0 = pb.R0;
            _zBottom = pb.ZBottom;
            _thickness = pb.Thickness;
            _height = pb.Height;
            TaperTop = new TaperViewModel(pb.TaperTop);
            TaperBottom = new TaperViewModel(pb.TaperBottom);

            // Surface nested taper edits as a change on this component so the owning
            // TransformerViewModel rebuilds the geometry (a non-Name change avoids a
            // structural tree refresh).
            TaperTop.PropertyChanged += OnTaperChanged;
            TaperBottom.PropertyChanged += OnTaperChanged;
        }

        private void OnTaperChanged(object? sender, PropertyChangedEventArgs e) =>
            OnPropertyChanged(ReferenceEquals(sender, TaperTop) ? nameof(TaperTop) : nameof(TaperBottom));

        public PressboardBarrier ToModel() =>
            new(Name, R0, ZBottom, Thickness, Height, TaperTop.ToModel(), TaperBottom.ToModel());
    }
}
