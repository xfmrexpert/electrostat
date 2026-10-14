using GeometryLib;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using TfmrLib.FEM;

namespace electrostat
{
    /// <summary>
    /// Semantic classification of a built <see cref="GeomSurface"/>, used by the geometry
    /// view to color each region by what it physically represents (rather than cycling a
    /// rotating palette by surface index).
    /// </summary>
    public enum SurfaceCategory
    {
        /// <summary>Unclassified surface; rendered with a neutral fallback fill.</summary>
        Other = 0,
        /// <summary>The outer oil/domain region.</summary>
        Oil,
        /// <summary>A winding conductor block.</summary>
        Winding,
        /// <summary>A pressboard barrier.</summary>
        Pressboard,
        /// <summary>An angle ring.</summary>
        AngleRing,
        /// <summary>The metal core of a static ring.</summary>
        StaticRingMetal,
        /// <summary>The paper wrap of a static ring.</summary>
        StaticRingPaper,
    }

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
        /// Map from each built <see cref="GeomSurface"/> to its semantic
        /// <see cref="SurfaceCategory"/>, so the geometry view can color regions by what
        /// they physically represent (e.g. pressboard vs. static-ring paper) instead of by
        /// a rotating index-based palette. Keyed by reference identity.
        /// </summary>
        public IReadOnlyDictionary<GeomSurface, SurfaceCategory> SurfaceCategories { get; init; } =
            new Dictionary<GeomSurface, SurfaceCategory>(ReferenceEqualityComparer.Instance);

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
        public sealed record ScenarioSolution(string Name, FieldSolution? Solution);

        /// <summary>
        /// Results of the most recent <see cref="Solve"/>, one entry per scenario in build
        /// order. Empty until <see cref="Solve"/> has run.
        /// </summary>
        public List<ScenarioSolution> ScenarioSolutions { get; } = new();

        /// <summary>
        /// Why the most recent <see cref="Solve"/> could not load results, or null when it
        /// loaded them. Use this rather than <see cref="NoumenaProblem.LastLoadError"/>: TfmrLib's
        /// reader only understands coupling-matrix archives and reports a spurious failure
        /// for every field run.
        /// </summary>
        public string? LoadError { get; private set; }

        /// <summary>
        /// Convenience accessor for the first solved scenario's field (null until a solve has
        /// run or if no scenario produced results).
        /// </summary>
        public FieldSolution? Solution =>
            ScenarioSolutions.FirstOrDefault(s => s.Solution != null)?.Solution;

        /// <summary>
        /// Solve the FEM problem as built, evaluating every voltage <see cref="Scenario"/>
        /// created during the build in a single solver invocation against the shared mesh,
        /// then collect each scenario's field into <see cref="ScenarioSolutions"/> (with
        /// human-readable physical names populated). The solver writes one HDF5 archive,
        /// "results.h5" next to the mesh, holding every scenario. Returns 0.
        /// </summary>
        public int Solve(IProgress<NoumenaProgressEvent>? progress = null)
        {
            ScenarioSolutions.Clear();
            LoadError = null;

            var problem = Problem;
            if (problem == null) return -1;
            var noumena = problem as NoumenaProblem;

            // Co-locate the input deck and the results archive with the mesh so every
            // build's files sit together. The results path must be absolute: NoumenaProblem
            // resolves a relative one against the deck's folder when writing it, but checks
            // it as-is when loading.
            var meshDir = !string.IsNullOrEmpty(problem.MeshPath)
                ? Path.GetDirectoryName(Path.GetFullPath(problem.MeshPath))
                : null;
            string? resultsFile = null;
            if (noumena != null && !string.IsNullOrEmpty(meshDir))
            {
                noumena.Filename = Path.Combine(meshDir, "case.json");
                resultsFile = Path.Combine(meshDir, "results.h5");
                noumena.ResultsFile = resultsFile;

                // Never load a previous run's archive if this solve fails to write one.
                if (File.Exists(resultsFile))
                    File.Delete(resultsFile);
            }

            // Run once: NoumenaProblem writes all Scenarios to its case.json and the solver
            // evaluates them all, reusing the factorized system between permutations.
            Action<NoumenaProgressEvent>? progressHandler = progress == null
                ? null
                : e => { if (!IsSpuriousLoadError(e)) progress.Report(e); };
            if (noumena != null && progressHandler != null)
                noumena.ProgressChanged += progressHandler;

            try
            {
                problem.Solve();
            }
            finally
            {
                if (noumena != null && progressHandler != null)
                    noumena.ProgressChanged -= progressHandler;
            }

            // NoumenaProblem.Results is ignored: TfmrLib's reader handles coupling-matrix
            // archives only. Read the scenario fields here instead.
            var loaded = new List<FieldSolution>();
            if (resultsFile == null)
            {
                LoadError = "The mesh path was not set, so there is no results file to read.";
            }
            else
            {
                try
                {
                    loaded = Hdf5FieldResultsReader.Read(resultsFile);
                }
                catch (Exception ex)
                {
                    LoadError = $"Failed to load FEM results from '{resultsFile}': {ex.Message}";
                }
            }
            if (LoadError != null)
                progress?.Report(new NoumenaProgressEvent(NoumenaProgressEventType.Message, Level: "error", Message: LoadError));

            foreach (var sol in loaded)
                PopulatePhysicalNames(sol);

            // No authored scenarios: surface whatever the solver produced.
            if (problem.Scenarios.Count == 0)
            {
                ScenarioSolutions.Add(new ScenarioSolution("Base", loaded.FirstOrDefault()));
                return 0;
            }

            // Match archive scenarios to authored ones by name, taking each archive entry
            // once so repeated names still pair up in solve order.
            var unclaimed = new List<FieldSolution>(loaded);
            foreach (var scenario in problem.Scenarios)
            {
                var sol = unclaimed.FirstOrDefault(s => s.ScenarioName == scenario.Name);
                if (sol != null)
                    unclaimed.Remove(sol);
                else if (LoadError == null)
                    Console.WriteLine($"No results for scenario '{scenario.Name}' in '{resultsFile}'.");

                ScenarioSolutions.Add(new ScenarioSolution(scenario.Name, sol));
            }

            return 0;
        }

        /// <summary>
        /// NoumenaProblem tries to load every results archive with TfmrLib's coupling-matrix
        /// reader, which fails on a field run and reports it as an error. Electrostat loads
        /// the archive itself and reports its own failures, so drop those messages.
        /// </summary>
        private static bool IsSpuriousLoadError(NoumenaProgressEvent e) =>
            e.EventType == NoumenaProgressEventType.Message &&
            e.Level == "error" &&
            e.Message != null &&
            (e.Message.StartsWith("Failed to load FEM results from", StringComparison.Ordinal) ||
             e.Message.StartsWith("Results file '", StringComparison.Ordinal));

        /// <summary>
        /// The solver's results archive carries no physical names, so populate
        /// <see cref="FieldSolution.PhysicalNames"/> from the names
        /// we already know at build time (regions + Dirichlet BCs). This lets downstream
        /// code (e.g. the streamline Wiedmann overlay) look up a human-readable material
        /// name for each physical tag.
        /// </summary>
        private void PopulatePhysicalNames(FieldSolution? sol)
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
