# electrostat

Electrostatic analysis of transformer insulation: builds a winding geometry, meshes
it with gmsh, solves it with Noumena, and traces field lines to assess stress and
margins.

This package is self-contained for .NET; nothing else from Microsoft needs to be
installed. Run `electrostat_UI` (Linux) or `electrostat_UI.exe` (Windows).

## Prerequisites

electrostat drives two external programs, which must be installed separately:

| Program | Version | Where electrostat looks for it |
|---------|---------|--------------------------------|
| [gmsh](https://gmsh.info) | 4.15 (tested); 4.12 is too old | `GMSH_PATH`, then `PATH` |
| [Noumena](https://github.com/Inductive-Reasoning/Noumena/releases) | v0.1.1 or later | `NOUMENA_EXE`, then `noumena` on `PATH` |

- **gmsh**: download it from gmsh.info, or `pip install gmsh`. Linux
  distribution packages can be too old: Ubuntu 24.04's 4.12 rejects mesh
  options electrostat sets. electrostat
  also looks for gmsh in `bin` folders relative to the working directory before
  `PATH`, so set `GMSH_PATH` if more than one gmsh is installed.
- **Noumena**: unpack the release package for your platform and either add its
  `bin` folder to `PATH` or point `NOUMENA_EXE` at the executable. Releases
  before v0.1.1 solve, but electrostat then falls back to straight-sided elements
  and low-order potentials.

## Units

Geometry is entered in millimetres. Fields are reported in kV/mm.
