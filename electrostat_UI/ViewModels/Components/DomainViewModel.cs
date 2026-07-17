using CommunityToolkit.Mvvm.ComponentModel;
using electrostat;

namespace electrostat_UI.ViewModels.Components
{
    public partial class DomainViewModel : ObservableObject
    {
        [ObservableProperty]
        [NotifyPropertyChangedFor(nameof(ValidationMessage))]
        [NotifyPropertyChangedFor(nameof(HasValidationError))]
        private double _rInner;

        [ObservableProperty]
        [NotifyPropertyChangedFor(nameof(ValidationMessage))]
        [NotifyPropertyChangedFor(nameof(HasValidationError))]
        private double _rOuter;

        [ObservableProperty]
        [NotifyPropertyChangedFor(nameof(ValidationMessage))]
        [NotifyPropertyChangedFor(nameof(HasValidationError))]
        private double _zLower;

        [ObservableProperty]
        [NotifyPropertyChangedFor(nameof(ValidationMessage))]
        [NotifyPropertyChangedFor(nameof(HasValidationError))]
        private double _zUpper;

        public string? ValidationMessage
        {
            get
            {
                if (ROuter <= RInner)
                    return "Outer radius must be greater than inner radius.";
                if (ZUpper <= ZLower)
                    return "Upper axial position must be greater than lower axial position.";
                return null;
            }
        }

        public bool HasValidationError => ValidationMessage != null;

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
