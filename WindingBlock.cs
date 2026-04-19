using System;
using System.Collections.Generic;
using System.Text;

namespace electrostat
{
    /// <summary>
    /// Electrode block: rectangle with TOP TWO corners filleted.
    /// Using (R0, ZBottom) as anchor:
    ///   radial extent: [R0, R0+Width]
    ///   axial extent:  [ZBottom, ZBottom+Height]
    /// </summary>
    public readonly record struct WindingBlock(
        string Name,
        double R0,
        double ZBottom,
        double Width,
        double Height,
        double FilletR
    );
}
