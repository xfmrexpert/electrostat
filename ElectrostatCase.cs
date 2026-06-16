namespace electrostat
{
    /// <summary>
    /// A named voltage permutation: overrides the voltage of one or more windings for a
    /// single solved scenario (e.g. an impulse applied at a particular terminal). Only
    /// winding voltages are specified here; wall and dielectric voltages always come from
    /// the case's base <see cref="ElectrostatCase.Voltages"/> map. Static rings nested
    /// beneath a winding (see <see cref="StaticRing.ParentWinding"/>) inherit that
    /// winding's scenario voltage automatically.
    /// </summary>
    /// <param name="Name">Display name of the scenario (e.g. "HV impulse").</param>
    /// <param name="WindingVoltages">Map of winding name to its voltage for this scenario.</param>
    public sealed record VoltageScenario(
        string Name,
        IReadOnlyDictionary<string, double> WindingVoltages);

    /// <summary>
    /// A single example case: a name plus the geometry inputs needed to build it.
    /// </summary>
    /// <param name="Scenarios">
    /// Optional list of voltage permutations to solve for this case. When null or empty
    /// the case is solved once using <paramref name="Voltages"/> as-is.
    /// </param>
    public sealed record ElectrostatCase(
        string Name,
        Domain Domain,
        IReadOnlyList<WindingBlock> Windings,
        IReadOnlyList<PressboardBarrier> Pressboards,
        IReadOnlyList<AngleRing> AngleRings,
        IReadOnlyList<StaticRing> StaticRings,
        Dictionary<string, double> Voltages,
        IReadOnlyList<VoltageScenario>? Scenarios = null)
    {
        /// <summary>
        /// Resolve the full electrode voltage map for a given <paramref name="scenario"/>.
        /// Starts from the base <see cref="Voltages"/>, overlays the scenario's per-winding
        /// overrides (pass <c>null</c> to resolve the base / no-scenario case), then
        /// propagates each winding's resulting voltage to any static ring nested beneath it
        /// via <see cref="StaticRing.ParentWinding"/> (the ring's <c>"{Name}_Metal"</c>
        /// entry).Voltages are taken from <see cref="Voltages"/>
        /// unchanged.
        /// </summary>
        public Dictionary<string, double> EffectiveVoltages(VoltageScenario? scenario = null)
        {
            var v = new Dictionary<string, double>(Voltages);

            if (scenario?.WindingVoltages != null)
            {
                foreach (var (winding, voltage) in scenario.WindingVoltages)
                    v[winding] = voltage;
            }

            // Nested static rings inherit their parent winding's (possibly overridden)
            // voltage. Unparented rings keep whatever "{Name}_Metal" entry the base map
            // already holds.
            foreach (var sr in StaticRings)
            {
                if (string.IsNullOrEmpty(sr.ParentWinding)) continue;
                if (v.TryGetValue(sr.ParentWinding, out var parentV))
                    v[sr.Name + "_Metal"] = parentV;
            }

            return v;
        }
    }
}
