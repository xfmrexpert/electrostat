using System;
using System.Collections.Generic;
using CommunityToolkit.Mvvm.ComponentModel;
using electrostat;
using TfmrLib.FEM;

namespace electrostat_UI.ViewModels.Components
{
    /// <summary>
    /// Editable view-model for a <see cref="Cut"/>: a slice of the parent transformer that
    /// varies only by its domain bounds, the solver coordinate system, and whether the
    /// adjacent phase is modeled. The owning <c>TransformerViewModel</c> observes this
    /// VM's (and its nested <see cref="Domain"/>'s) <c>PropertyChanged</c> to rebuild
    /// geometry / refresh the explorer.
    /// </summary>
    public partial class CutViewModel : ComponentViewModel
    {
        /// <summary>Domain bounds this cut is solved over. Edited via the shared domain editor.</summary>
        public DomainViewModel Domain { get; }

        [ObservableProperty] private GeometryType _geometryType;
        [ObservableProperty] private bool _includeAdjacentPhase;

        /// <summary>Choices for the geometry-type picker (axisymmetric vs planar).</summary>
        public IReadOnlyList<GeometryType> AvailableGeometryTypes { get; } =
            (GeometryType[])Enum.GetValues(typeof(GeometryType));

        public CutViewModel()
        {
            Name = "Cut";
            Domain = new DomainViewModel();
        }

        public CutViewModel(Cut cut)
        {
            Name = cut.Name;
            Domain = new DomainViewModel(cut.Domain);
            _geometryType = cut.GeometryType;
            _includeAdjacentPhase = cut.IncludeAdjacentPhase;
        }

        public Cut ToModel() => new(Name, Domain.ToModel(), GeometryType, IncludeAdjacentPhase);
    }
}
