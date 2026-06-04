using CommunityToolkit.Mvvm.ComponentModel;

namespace electrostat_UI.ViewModels.Components
{
    /// <summary>
    /// Base for editable component view-models exposed in the case tree
    /// (windings, pressboards, angle rings, static rings, etc.).
    /// </summary>
    public abstract partial class ComponentViewModel : ObservableObject
    {
        [ObservableProperty] private string _name = string.Empty;

        /// <summary>Stable key used to look up generated surfaces for highlighting.</summary>
        public virtual string SurfaceKey => Name;

        public string DisplayName => Name;
    }
}
