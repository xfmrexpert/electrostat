using System.Collections.Generic;
using CommunityToolkit.Mvvm.ComponentModel;
using electrostat;

namespace electrostat_UI.ViewModels.Components
{
    /// <summary>
    /// Editable wrapper around a <see cref="Taper"/>. A taper is optional, so
    /// <see cref="Enabled"/> toggles between a tapered end and a plain square end
    /// (in which case <see cref="ToModel"/> returns <c>null</c>). Reused by the
    /// pressboard (top / bottom) and angle-ring (V-tip) editors.
    /// </summary>
    public partial class TaperViewModel : ObservableObject
    {
        /// <summary>The radial sides offered in the <see cref="Side"/> picker.</summary>
        public static IReadOnlyList<string> SideOptions { get; } = new[] { "inner", "outer" };

        /// <summary>When false the end is square and <see cref="ToModel"/> returns null.</summary>
        [ObservableProperty] private bool _enabled;

        /// <summary>Axial length of the tapered region (mm).</summary>
        [ObservableProperty] private double _length;

        /// <summary>Thickness at the tip of the taper (mm).</summary>
        [ObservableProperty] private double _endThickness;

        /// <summary>Which radial side is sloped: "inner" (lower r) or "outer" (higher r).</summary>
        [ObservableProperty] private string _side = "inner";

        public TaperViewModel() { }

        public TaperViewModel(Taper? taper)
        {
            if (taper is { } t)
            {
                _enabled = true;
                _length = t.Length;
                _endThickness = t.EndThickness;
                _side = string.IsNullOrEmpty(t.Side) ? "inner" : t.Side;
            }
        }

        /// <summary>Returns the edited taper, or null when <see cref="Enabled"/> is false.</summary>
        public Taper? ToModel() =>
            Enabled ? new Taper(Length, EndThickness, Side) : null;
    }
}
