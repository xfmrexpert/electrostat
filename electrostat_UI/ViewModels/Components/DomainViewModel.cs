using CommunityToolkit.Mvvm.ComponentModel;
using electrostat;

namespace electrostat_UI.ViewModels.Components
{
    public partial class DomainViewModel : ObservableObject
    {
        [ObservableProperty] private double _rInner;
        [ObservableProperty] private double _rOuter;
        [ObservableProperty] private double _zLower;
        [ObservableProperty] private double _zUpper;

        public DomainViewModel() { }

        public DomainViewModel(Domain d)
        {
            _rInner = d.RInner;
            _rOuter = d.ROuter;
            _zLower = d.ZLower;
            _zUpper = d.ZUpper;
        }

        public Domain ToModel() => new(RInner, ROuter, ZLower, ZUpper);

        public string DisplayName => "Domain";
    }
}
