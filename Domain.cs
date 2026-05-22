using System;
using System.Collections.Generic;
using System.Text;
using GeometryLib;

namespace electrostat
{
    public readonly record struct Domain(
        double RInner,
        double ROuter,
        double ZLower,
        double ZUpper
    )
    {
        /// <summary>
        /// Check if a point (r, z) is inside the domain bounds.
        /// </summary>
        public bool Contains(double r, double z)
            => r >= RInner && r <= ROuter && z >= ZLower && z <= ZUpper;

        /// <summary>
        /// Check if a bounding box intersects with the domain.
        /// </summary>
        public bool Intersects(double minR, double minZ, double maxR, double maxZ)
            => !(maxR < RInner || minR > ROuter || maxZ < ZLower || minZ > ZUpper);

        /// <summary>
        /// Check if a GeometryLib BoundingBox intersects with the domain.
        /// </summary>
        public bool Intersects(BoundingBox bbox)
            => Intersects(bbox.MinX, bbox.MinY, bbox.MaxX, bbox.MaxY);

        /// <summary>
        /// Clamp a point to the domain boundaries.
        /// </summary>
        public (double r, double z) Clamp(double r, double z)
            => (Math.Clamp(r, RInner, ROuter), Math.Clamp(z, ZLower, ZUpper));

        /// <summary>
        /// Convert domain to a BoundingBox for geometry operations.
        /// </summary>
        public BoundingBox ToBoundingBox()
            => new BoundingBox(RInner, ZLower, ROuter, ZUpper);

        /// <summary>
        /// Check if a bounding box is entirely within the domain.
        /// </summary>
        public bool FullyContains(double minR, double minZ, double maxR, double maxZ)
            => minR >= RInner && maxR <= ROuter && minZ >= ZLower && maxZ <= ZUpper;
    }
}
