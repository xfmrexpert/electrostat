namespace electrostat
{
    /// <summary>
    /// Point-evaluator for a 2D scalar/vector field defined over an FEM mesh.
    /// The reference frame is the mesh's native (x, y) plane; for axisymmetric
    /// electrostatic problems this is (r, z).
    /// </summary>
    public interface IFieldSampler
    {
        /// <summary>
        /// Try to evaluate the electric field at <paramref name="x"/>,<paramref name="y"/>.
        /// Components are returned in kV/mm.
        /// </summary>
        /// <param name="ex">Out: field x-component in kV/mm (Er for axisymmetric).</param>
        /// <param name="ey">Out: field y-component in kV/mm (Ez for axisymmetric).</param>
        /// <param name="elementId">Out: 1-based Gmsh element id containing the point, or 0 if outside.</param>
        /// <param name="materialTag">Out: physical (material) tag of the containing element, or 0 if outside.</param>
        /// <returns><c>true</c> if the point lies inside the meshed domain.</returns>
        bool TryEvaluateE(double x, double y, out double ex, out double ey,
            out int elementId, out int materialTag);

        /// <summary>
        /// Try to evaluate the scalar potential V at the given point.
        /// </summary>
        bool TryEvaluateV(double x, double y, out double v, out int elementId, out int materialTag);
    }
}
