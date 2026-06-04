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

        // Tapers are kept as immutable copies for now (UI editing TBD).
        public Taper? TaperTop { get; set; }
        public Taper? TaperBottom { get; set; }

        public PressboardViewModel() { Name = "Pressboard"; }

        public PressboardViewModel(PressboardBarrier pb)
        {
            Name = pb.Name;
            _r0 = pb.R0;
            _zBottom = pb.ZBottom;
            _thickness = pb.Thickness;
            _height = pb.Height;
            TaperTop = pb.TaperTop;
            TaperBottom = pb.TaperBottom;
        }

        public PressboardBarrier ToModel() =>
            new(Name, R0, ZBottom, Thickness, Height, TaperTop, TaperBottom);
    }
}
