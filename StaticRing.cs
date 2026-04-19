using System;
using System.Collections.Generic;
using System.Text;

namespace electrostat
{
    /// <summary>
    /// Electrostatic (static) ring electrode with a paper wrap.
    /// </summary>
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
        double TPaper = 2.0
    );
}
