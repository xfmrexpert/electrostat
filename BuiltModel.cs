using GeometryLib;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using TfmrLib.FEM;

namespace electrostat
{
    /// <summary>
    /// Result of a single <see cref="GeometryBuilder"/> build. Owns the geometry, the FEM
    /// problem, and the lookup caches that used to live as mutable statics on
    /// <see cref="GeometryBuilder"/>. A fresh instance is produced per build, so successive
    /// or concurrent builds no longer clobber shared global state.
    /// </summary>
    public sealed class BuiltModel
    {
        /// <summary>The geometry produced by the build (post clip / conditioning).</summary>
        public required Geometry Geometry { get; init; }

        /// <summary>
        /// The FEM problem (materials, regions, terminals, BCs) created for this build.
        /// Null only if a build failed before problem creation.
        /// </summary>
        public FEMProblem? Problem { get; init; }

        /// <summary>
        /// Map from a component's case-defined name (e.g. "HV", "PB_inner",
        /// "SR_TOP_HV_inner_Metal") to the GeomSurface(s) it produced, so the UI can
        /// highlight selected components in the geometry view.
        /// </summary>
        public Dictionary<string, List<GeomSurface>> ComponentSurfaces { get; init; } = new();

        /// <summary>
        /// Surfaces that act as electrodes (Dirichlet-V conductors): windings and
        /// static-ring metals. Used by analysis features (e.g. streamline tracing) that
        /// should terminate only on conductor boundaries rather than on dielectric
        /// (pressboard / paper) interfaces.
        /// </summary>
        public List<GeomSurface> ElectrodeSurfaces { get; init; } = new();

        /// <summary>
        /// Outer-domain wall edges that carry a Dirichlet BC (e.g. core / tank / yoke).
        /// Each loop is a single-segment loop wrapping one boundary line so the streamline
        /// clipper can terminate traces against them the same way it terminates against
        /// electrode surfaces.
        /// </summary>
        public List<GeomLineLoop> DirichletWallLoops { get; init; } = new();

        /// <summary>
        /// One solved voltage permutation: the scenario's name paired with the field the
        /// solver produced for it. Populated by <see cref="Solve"/>, one entry per
        /// <see cref="FEMProblem.Scenarios"/> the build created.
        /// </summary>
        /// <param name="Name">Scenario display name (matches the authored scenario / "Base").</param>
        /// <param name="Solution">The loaded field, or null if that scenario's results were missing.</param>
        public sealed record ScenarioSolution(string Name, FEMSolution? Solution);

        /// <summary>
        /// Results of the most recent <see cref="Solve"/>, one entry per scenario in build
        /// order. Empty until <see cref="Solve"/> has run.
        /// </summary>
        public List<ScenarioSolution> ScenarioSolutions { get; } = new();

        /// <summary>
        /// Convenience accessor for the first solved scenario's field (null until a solve has
        /// run or if no scenario produced results).
        /// </summary>
        public FEMSolution? Solution =>
            ScenarioSolutions.FirstOrDefault(s => s.Solution != null)?.Solution;

        /// <summary>
        /// Solve the FEM problem as built, evaluating every voltage <see cref="Scenario"/>
        /// created during the build in a single solver invocation against the shared mesh,
        /// then collect each scenario's field into <see cref="ScenarioSolutions"/> (with
        /// human-readable physical names populated). The solver writes one
        /// "&lt;scenarioName&gt;.results.msh" next to the mesh, so each scenario's results are
        /// loaded from that path. Returns 0.
        /// </summary>
        public int Solve()
        {
            ScenarioSolutions.Clear();

            var problem = Problem;
            if (problem == null) return -1;

            // The solver writes each scenario's "<scenarioName>.results.msh" next to the
            // shared mesh, not next to the case.json. Co-locate the input deck there too so
            // every build's deck sits beside its mesh and results.
            var meshDir = !string.IsNullOrEmpty(problem.MeshPath)
                ? Path.GetDirectoryName(problem.MeshPath)
                : null;
            if (problem is MFEMProblem mfem && !string.IsNullOrEmpty(meshDir))
            {
                mfem.Filename = Path.Combine(meshDir, "case.json");
            }

            // Run once: MFEMProblem writes all Scenarios to its case.json and the solver
            // evaluates them all, reusing the factorized system between permutations.
            problem.Solve();

            // No authored scenarios: surface whatever the problem loaded directly.
            if (problem.Scenarios.Count == 0)
            {
                var only = problem.Solution;
                PopulatePhysicalNames(only);
                ScenarioSolutions.Add(new ScenarioSolution("Base", only));
                return 0;
            }

            // Collect each scenario's field from the per-scenario results file the solver
            // wrote next to the mesh.
            foreach (var scenario in problem.Scenarios)
            {
                FEMSolution? sol = null;
                if (!string.IsNullOrEmpty(meshDir))
                {
                    var resultsPath = Path.Combine(meshDir, scenario.Name + ".results.msh");
                    if (File.Exists(resultsPath))
                    {
                        try { sol = FEMSolution.Load(resultsPath); }
                        catch (Exception ex)
                        {
                            Console.WriteLine($"Failed to load results for scenario '{scenario.Name}' from '{resultsPath}': {ex.Message}");
                        }
                    }
                    else
                    {
                        Console.WriteLine($"No results file for scenario '{scenario.Name}' at '{resultsPath}'.");
                    }
                }

                PopulatePhysicalNames(sol);
                ScenarioSolutions.Add(new ScenarioSolution(scenario.Name, sol));
            }

            return 0;
        }

        /// <summary>
        /// The MFEM solver does not currently emit a $PhysicalNames section in its
        /// results.msh, so populate <see cref="FEMSolution.PhysicalNames"/> from the names
        /// we already know at build time (regions + Dirichlet BCs). This lets downstream
        /// code (e.g. the streamline Wiedmann overlay) look up a human-readable material
        /// name for each physical tag.
        /// </summary>
        private void PopulatePhysicalNames(FEMSolution? sol)
        {
            if (sol == null) return;

            // Physical tags (MFEM attribute IDs) live on the EntityGroups, keyed by group
            // name. Regions and boundary conditions don't carry tags directly — they
            // reference their group via EntityGroupName — so resolve the tags through it.
            var groupTags = new Dictionary<string, List<int>>(StringComparer.Ordinal);
            foreach (var g in Problem!.EntityGroups)
            {
                if (string.IsNullOrEmpty(g.Name)) continue;
                if (!groupTags.TryGetValue(g.Name, out var list))
                    groupTags[g.Name] = list = new List<int>();
                list.AddRange(g.AttributeIds);
            }

            foreach (var r in Problem.Regions)
            {
                if (string.IsNullOrEmpty(r.Name)) continue;
                if (!groupTags.TryGetValue(r.EntityGroupName, out var tags)) continue;
                foreach (var t in tags)
                    sol.PhysicalNames[t] = r.Name;
            }
            foreach (var bc in Problem.BoundaryConditions)
            {
                if (string.IsNullOrEmpty(bc.Name)) continue;
                if (!groupTags.TryGetValue(bc.EntityGroupName, out var tags)) continue;
                foreach (var t in tags)
                    sol.PhysicalNames.TryAdd(t, bc.Name);
            }
        }
    }
}
