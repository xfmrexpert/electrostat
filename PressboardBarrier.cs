using System;
using System.Collections.Generic;
using System.Text;

namespace electrostat
{
    /// <summary>
    /// Pressboard barrier (axisymmetric cylinder wall) represented in r–z as a solid rectangle,
    /// optionally with tapered ends.
    /// </summary>
    public readonly record struct PressboardBarrier(
        string Name,
        double R0,
        double ZBottom,
        double Thickness,
        double Height,
        Taper? TaperTop = null,
        Taper? TaperBottom = null
    );
}
