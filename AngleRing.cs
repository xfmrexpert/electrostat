using System;
using System.Collections.Generic;
using System.Text;

namespace electrostat
{
    /// <summary>
    /// Axisymmetric angle ring (pressboard L-shape) in r–z.
    /// </summary>
    public readonly record struct AngleRing(
        string Name,
        double R0,
        double ZCorner,
        double Tv,        // vertical leg thickness (radial)
        double Hv,        // vertical leg height (axial, signed)
        double Th,        // horizontal leg thickness (axial)
        double Wh,        // horizontal leg width (radial, signed)
        double InsideFilletR = 0.0,
        Taper? TaperVTip = null
    );

}
