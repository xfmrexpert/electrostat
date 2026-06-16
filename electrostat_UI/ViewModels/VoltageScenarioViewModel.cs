using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using CommunityToolkit.Mvvm.ComponentModel;
using electrostat;

namespace electrostat_UI.ViewModels
{
    /// <summary>
    /// One winding's voltage within a <see cref="VoltageScenarioViewModel"/>. The winding
    /// name is fixed (it mirrors a winding in the case); only <see cref="Voltage"/> is
    /// edited by the user.
    /// </summary>
    public partial class ScenarioVoltageCell : ObservableObject
    {
        [ObservableProperty] private string _windingName;
        [ObservableProperty] private double _voltage;

        public ScenarioVoltageCell(string windingName, double voltage)
        {
            _windingName = windingName;
            _voltage = voltage;
        }
    }

    /// <summary>
    /// Editable view-model for a <see cref="VoltageScenario"/>: a named voltage permutation
    /// holding one editable cell per winding. Nested static rings inherit their parent
    /// winding's voltage automatically (resolved at solve time), so only windings appear
    /// here.
    /// </summary>
    public partial class VoltageScenarioViewModel : ObservableObject
    {
        [ObservableProperty] private string _name;

        /// <summary>One voltage cell per winding, in winding order.</summary>
        public ObservableCollection<ScenarioVoltageCell> Cells { get; } = new();

        public VoltageScenarioViewModel(string name)
        {
            _name = name;
        }

        public VoltageScenarioViewModel(VoltageScenario model, IEnumerable<string> windingNames)
        {
            _name = model.Name;
            foreach (var wn in windingNames)
            {
                double v = model.WindingVoltages != null && model.WindingVoltages.TryGetValue(wn, out var x)
                    ? x
                    : 0.0;
                Cells.Add(new ScenarioVoltageCell(wn, v));
            }
        }

        /// <summary>
        /// Reconcile the cell list with the current set of winding names: add cells for new
        /// windings (default 0 V), drop cells whose winding no longer exists, and reorder to
        /// match. Returns true if anything changed.
        /// </summary>
        public bool SyncWindings(IReadOnlyList<string> windingNames)
        {
            var byName = Cells.ToDictionary(c => c.WindingName);
            bool changed = false;

            // Remove cells for windings that no longer exist.
            for (int i = Cells.Count - 1; i >= 0; i--)
            {
                if (!windingNames.Contains(Cells[i].WindingName))
                {
                    Cells.RemoveAt(i);
                    changed = true;
                }
            }

            // Ensure a cell exists for each winding, in order.
            for (int i = 0; i < windingNames.Count; i++)
            {
                string wn = windingNames[i];
                if (!byName.TryGetValue(wn, out var cell))
                {
                    cell = new ScenarioVoltageCell(wn, 0.0);
                    Cells.Insert(System.Math.Min(i, Cells.Count), cell);
                    changed = true;
                }
                else
                {
                    int current = Cells.IndexOf(cell);
                    if (current != i && i < Cells.Count)
                    {
                        Cells.Move(current, i);
                        changed = true;
                    }
                }
            }

            return changed;
        }

        public VoltageScenario ToModel()
        {
            var map = new Dictionary<string, double>();
            foreach (var c in Cells)
                map[c.WindingName] = c.Voltage;
            return new VoltageScenario(Name, map);
        }
    }
}
