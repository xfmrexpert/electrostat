using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using electrostat;

namespace electrostat_UI.ViewModels.Components
{
    public partial class StaticRingViewModel : ComponentViewModel
    {
        /// <summary>Sentinel shown in the parent-winding picker for an unparented ring.</summary>
        public const string NoneWinding = "(none)";

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

        /// <summary>
        /// Name of the winding this ring is nested beneath, or <see cref="NoneWinding"/>
        /// when the ring is independent. A parented ring inherits its parent winding's
        /// voltage in every solved scenario, so its own <see cref="Voltage"/> is ignored.
        /// </summary>
        [ObservableProperty]
        [NotifyPropertyChangedFor(nameof(IsVoltageInherited))]
        [NotifyPropertyChangedFor(nameof(IsVoltageEditable))]
        private string _parentWinding = NoneWinding;

        /// <summary>
        /// Candidate winding names for the parent picker, including <see cref="NoneWinding"/>.
        /// The owning <c>ElectrostatCaseViewModel</c> keeps this in sync with the case's
        /// windings and shares one instance across every ring.
        /// </summary>
        [ObservableProperty] private ObservableCollection<string> _availableWindings = new() { NoneWinding };

        /// <summary>True when the ring inherits its voltage from a parent winding.</summary>
        public bool IsVoltageInherited => !string.IsNullOrEmpty(ParentWinding) && ParentWinding != NoneWinding;

        /// <summary>True when the ring's own <see cref="Voltage"/> is used (not inherited).</summary>
        public bool IsVoltageEditable => !IsVoltageInherited;

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
            _parentWinding = string.IsNullOrEmpty(sr.ParentWinding) ? NoneWinding : sr.ParentWinding;
        }

        public StaticRing ToModel() =>
            new(Name, R0, ZBottom, Width, Height, RTL, RTR, RBR, RBL, TPaper,
                ParentWinding == NoneWinding ? null : ParentWinding);
    }
}
