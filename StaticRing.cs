using System;
using System.Collections.Generic;
using System.Text;

namespace electrostat
{
    /// <summary>
    /// Electrostatic (static) ring electrode with a paper wrap.
    /// </summary>
    /// <remarks>
    /// <see cref="ParentWinding"/> nests the ring beneath a winding: when set to a
    /// winding's name, the ring's metal electrode inherits that winding's voltage in
    /// every solved scenario (see <c>ElectrostatCase.EffectiveVoltages</c>). When null
    /// the ring keeps its own independent voltage from the case's voltage map.
    /// </remarks>
    public readonly record struct StaticRing(
        string Name,
        double R0,
        double ZBottom,
        double Width,
        double Height,
        double RTL = 0.0,
        double RTR = 0.0,
        double RBR = 0.0,
        double RBL = 0.0,
        double TPaper = 2.0,
        string? ParentWinding = null
    );
}
