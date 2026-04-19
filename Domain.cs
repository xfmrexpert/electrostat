using System;
using System.Collections.Generic;
using System.Text;

namespace electrostat
{
    public readonly record struct Domain(
        double RInner,
        double ROuter,
        double ZLower,
        double ZUpper
    );
}
