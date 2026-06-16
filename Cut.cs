using TfmrLib.FEM;

namespace electrostat
{
    /// <summary>
    /// A single slice ("cut") of a transformer's 3D geometry. A cut shares all of its
    /// parent <see cref="Transformer"/>'s components, dimensions, voltages and scenarios;
    /// it varies only by the domain bounds it is solved over, the coordinate system used,
    /// and whether the adjacent phase (with its interphase insulation) is modeled.
    /// </summary>
    /// <param name="Name">Display name of the cut (e.g. "Tank Cut - LV").</param>
    /// <param name="Domain">The computational domain bounds for this cut.</param>
    /// <param name="GeometryType">
    /// Coordinate system the MFEM solver should assume. The mesh is identical across cuts;
    /// only this flag changes (e.g. a single-phase window is <see cref="GeometryType.Axisymmetric"/>,
    /// a two-phase window cut is <see cref="GeometryType.Planar"/>).
    /// </param>
    /// <param name="IncludeAdjacentPhase">
    /// When true the parent transformer mirrors its components about the window centerline
    /// and adds the interphase insulation, so the cut models two phases instead of one.
    /// </param>
    public sealed record Cut(
        string Name,
        Domain Domain,
        GeometryType GeometryType = GeometryType.Axisymmetric,
        bool IncludeAdjacentPhase = false);
}
