# Domain-Based Geometry Clipping Usage Guide

## Overview
The electrostat project now supports automatic geometry clipping to the computational domain. This allows you to define the full transformer geometry while only meshing and simulating the region of interest.

## Changes Made

### 1. Domain.cs Extensions
Added spatial query methods:
- `Contains(r, z)` - Check if point is inside domain
- `Intersects(BoundingBox)` - Check if geometry intersects domain
- `FullyContains(...)` - Check if geometry is entirely within domain
- `Clamp(r, z)` - Clamp coordinates to domain boundaries
- `ToBoundingBox()` - Convert to BoundingBox type

### 2. GeometryClipping.cs (New)
Comprehensive clipping utilities:
- **Cohen-Sutherland line clipping** - Fast rectangular clipping for line segments
- **Sutherland-Hodgman polygon clipping** - Multi-edge clipping for closed loops
- **Arc clipping** - Conservative bounding box approach
- **Full geometry clipping** - `ClipGeometryToDomain()` for complete workflows

### 3. GeometryBuilder.cs Enhancements
Modified `BuildModel` with three-phase pipeline:

**Phase 1: Unrestricted Construction**
- Build full transformer geometry without domain constraints
- All components created at their actual positions

**Phase 2: Smart Clipping** (optional via `clipToDomain` parameter)
- Components entirely within domain → no clipping (fast path)
- Components partially outside → clip to domain bounds
- Components entirely outside → skip
- Console logging shows clipped/skipped components

**Phase 3: Meshing**
- Mesh only the domain-clipped geometry

### 4. Program.cs Updates
All `BuildModel` calls now explicitly enable clipping:
```csharp
GeometryBuilder.BuildModel(
    dom, windings, pressboards, angleRings, staticRings, 
    lc: 5.0, 
    mshOut: $"{caseName}/geom.msh", 
    clipToDomain: true  // NEW: Enable domain clipping
);
```

## How It Works

### Example: Window Cut Domain
```csharp
var dom = new Domain(
    RInner: 510.0 / 2,      // Inner radius
    ROuter: 672.0,          // Outer radius  
    ZLower: 1000.0,         // Bottom axial position
    ZUpper: 1500.0          // Top axial position
);

// Full transformer geometry is defined
var windings = new List<WindingBlock> { ... };
var pressboards = new List<PressboardBarrier> { ... };

// BuildModel clips geometry to domain before meshing
GeometryBuilder.BuildModel(dom, windings, pressboards, ..., clipToDomain: true);
```

**What happens:**
1. All transformer components are created at their full size
2. Components are checked against domain bounds
3. Components outside domain are clipped or skipped
4. Only the domain region is meshed
5. Console shows: "Info: Clipping HV (winding) to domain bounds"

## Console Output Example
```
=== Building Window Cut (no adjacent phase) ===
Info: Clipping HV (winding) to domain bounds
Info: PB_inner (pressboard) is entirely outside domain, skipping
Info: Clipping AR_HV_lower (anglering) to domain bounds
Domain clipping summary: 5 components clipped, 2 components skipped
```

## Clipping Control

### Enable Clipping (Default)
```csharp
GeometryBuilder.BuildModel(..., clipToDomain: true);  // Explicit
GeometryBuilder.BuildModel(...);                      // Implicit (defaults to true)
```

### Disable Clipping
```csharp
GeometryBuilder.BuildModel(..., clipToDomain: false);
```

## Benefits

✅ **Define geometry freely** - No need to manually clip components  
✅ **Multiple domains** - Same geometry, different clipping regions  
✅ **Computational efficiency** - Mesh only region of interest  
✅ **Automatic boundaries** - Proper domain edges created at clip points  
✅ **Performance optimized** - Fast-path for fully-contained geometry  
✅ **Diagnostic output** - See what gets clipped/skipped  

## Domain Configurations in Program.cs

The code builds 6 different domain configurations:

1. **Window Cut (no adj)** - Narrow radial window around core
2. **Tank Cut - LV** - Wider radial domain to LV winding
3. **Tank Cut - PA** - Full radial and extended axial domain  
4. **Tank Cut - End** - Narrow radial, extended axial
5. **Window Cut (adj phase)** - Window with adjacent phase
6. **Planar Model** - Same as #5 but with planar analysis

Each uses the same transformer geometry definition but clips to different domains.

## Technical Details

### Clipping Algorithms

**Lines** - Cohen-Sutherland algorithm
- Encodes points by region (inside/left/right/top/bottom)
- Clips line segments to rectangular domain
- O(1) for trivial reject/accept, O(n) for clipping

**Polygons** - Sutherland-Hodgman algorithm  
- Clips against each edge of domain rectangle in sequence
- Preserves polygon orientation
- Handles convex clip regions (rectangles)

**Arcs** - Conservative approximation
- Computes bounding box from endpoints and radius
- Clamps endpoints if partially outside
- TODO: Implement exact arc-rectangle intersection

### Limitations

- Arc clipping is approximate (uses bounding box)
- Holes in surfaces may need special handling
- Very small clipped regions may become degenerate

## Future Enhancements

- [ ] Exact arc-rectangle intersection clipping
- [ ] Tessellation of arcs before clipping
- [ ] Support for non-rectangular domains
- [ ] Visualization of clipped vs. original geometry
