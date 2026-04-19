using System;
using System.Collections.Generic;
using System.Text;

namespace electrostat
{
    /// <summary>
    /// Taper specification for an end of a pressboard barrier or angle ring.
    /// Length: axial length of the tapered region (mm)
    /// EndThickness: thickness at the tip of the taper (mm)
    /// Side: which radial side is tapered - "inner" or "outer"
    ///       "inner" means the inner (lower r) edge is sloped
    ///       "outer" means the outer (higher r) edge is sloped
    /// </summary>
    public readonly record struct Taper(
        double Length,
        double EndThickness,
        string Side
    );
}
