using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.ComponentModel;
using System.Linq;
using CommunityToolkit.Mvvm.ComponentModel;
using electrostat;
using electrostat_UI.ViewModels.Components;

namespace electrostat_UI.ViewModels
{
    /// <summary>
    /// Editable view-model for an <see cref="ElectrostatCase"/>. Wraps each component
    /// in an observable VM and raises <see cref="Changed"/> whenever any nested
    /// property mutates so the view can rebuild the geometry.
    /// </summary>
    public partial class ElectrostatCaseViewModel : ObservableObject
    {
        [ObservableProperty] private string _name;

        public DomainViewModel Domain { get; }
        public ObservableCollection<WindingViewModel> Windings { get; } = new();
        public ObservableCollection<PressboardViewModel> Pressboards { get; } = new();
        public ObservableCollection<AngleRingViewModel> AngleRings { get; } = new();
        public ObservableCollection<StaticRingViewModel> StaticRings { get; } = new();

        /// <summary>
        /// Voltage permutations solved together for this case. Empty means a single solve
        /// using the base <see cref="Voltages"/> + per-component voltages.
        /// </summary>
        public ObservableCollection<VoltageScenarioViewModel> Scenarios { get; } = new();

        /// <summary>
        /// Current winding names plus the "(none)" sentinel, shared with every static
        /// ring's parent picker. Kept in sync as windings are added / removed / renamed.
        /// </summary>
        public ObservableCollection<string> AvailableWindings { get; } =
            new() { StaticRingViewModel.NoneWinding };

        public Dictionary<string, double> Voltages { get; }

        /// <summary>Raised whenever the case changes structurally or any field mutates.</summary>
        public event Action? Changed;

        /// <summary>
        /// Raised only when the case's tree structure or a node label changes (add / remove /
        /// rename / re-parent). The case explorer is rebuilt for these edits but left intact
        /// for pure value edits, so its expansion and selection survive routine editing.
        /// </summary>
        public event Action? StructureChanged;

        public ElectrostatCaseViewModel(ElectrostatCase model)
        {
            _name = model.Name;
            Domain = new DomainViewModel(model.Domain);
            Voltages = new Dictionary<string, double>(model.Voltages);

            foreach (var w in model.Windings)
                Windings.Add(new WindingViewModel(w, GetVoltage(model.Voltages, w.Name)));

            foreach (var pb in model.Pressboards)
                Pressboards.Add(new PressboardViewModel(pb));

            foreach (var ar in model.AngleRings)
                AngleRings.Add(new AngleRingViewModel(ar));

            foreach (var sr in model.StaticRings)
                StaticRings.Add(new StaticRingViewModel(sr, GetVoltage(model.Voltages, sr.Name + "_Metal")));

            // Seed the shared winding-name list and hand it to each ring's parent picker.
            SyncAvailableWindings();
            foreach (var sr in StaticRings)
                sr.AvailableWindings = AvailableWindings;

            if (model.Scenarios != null)
            {
                var windingNames = Windings.Select(w => w.Name).ToList();
                foreach (var sc in model.Scenarios)
                    Scenarios.Add(new VoltageScenarioViewModel(sc, windingNames));
            }

            Hook(Domain);
            HookCollection(Windings);
            HookCollection(Pressboards);
            HookCollection(AngleRings);
            HookCollection(StaticRings);
            HookScenarioCollection(Scenarios);

            // Keep the shared winding-name list current as windings are added / removed,
            // and ensure any ring added later (e.g. via the Add Static Ring command) shares
            // the same picker list instance.
            Windings.CollectionChanged += (_, _) => SyncAvailableWindings();
            StaticRings.CollectionChanged += (_, e) =>
            {
                if (e.NewItems != null)
                    foreach (StaticRingViewModel sr in e.NewItems)
                        sr.AvailableWindings = AvailableWindings;
            };
        }

        private static double GetVoltage(IDictionary<string, double> v, string name)
            => v.TryGetValue(name, out var x) ? x : 0.0;

        /// <summary>
        /// Reconcile <see cref="AvailableWindings"/> with the current windings in place
        /// (preserving the leading "(none)" sentinel and untouched entries so bound
        /// ComboBox selections are not disrupted), then re-sync every scenario's cells.
        /// </summary>
        private void SyncAvailableWindings()
        {
            var names = Windings.Select(w => w.Name).ToList();
            var desired = new List<string> { StaticRingViewModel.NoneWinding };
            desired.AddRange(names);

            for (int i = AvailableWindings.Count - 1; i >= 0; i--)
                if (!desired.Contains(AvailableWindings[i]))
                    AvailableWindings.RemoveAt(i);

            for (int i = 0; i < desired.Count; i++)
            {
                if (i < AvailableWindings.Count && AvailableWindings[i] == desired[i]) continue;
                if (AvailableWindings.Contains(desired[i]))
                {
                    int cur = AvailableWindings.IndexOf(desired[i]);
                    AvailableWindings.Move(cur, Math.Min(i, AvailableWindings.Count - 1));
                }
                else
                {
                    AvailableWindings.Insert(Math.Min(i, AvailableWindings.Count), desired[i]);
                }
            }

            foreach (var sc in Scenarios)
                sc.SyncWindings(names);
        }

        private void HookScenarioCollection(ObservableCollection<VoltageScenarioViewModel> coll)
        {
            foreach (var sc in coll) HookScenario(sc);
            coll.CollectionChanged += (_, e) =>
            {
                if (e.NewItems != null)
                    foreach (VoltageScenarioViewModel sc in e.NewItems)
                    {
                        sc.SyncWindings(Windings.Select(w => w.Name).ToList());
                        HookScenario(sc);
                    }
                if (e.OldItems != null)
                    foreach (VoltageScenarioViewModel sc in e.OldItems)
                        UnhookScenario(sc);
                RaiseStructureChanged();
            };
        }

        private void HookScenario(VoltageScenarioViewModel sc)
        {
            sc.PropertyChanged -= ItemChanged;
            sc.PropertyChanged += ItemChanged;
            foreach (var cell in sc.Cells) HookCell(cell);
            sc.Cells.CollectionChanged -= ScenarioCellsChanged;
            sc.Cells.CollectionChanged += ScenarioCellsChanged;
        }

        private void UnhookScenario(VoltageScenarioViewModel sc)
        {
            sc.PropertyChanged -= ItemChanged;
            foreach (var cell in sc.Cells) UnhookCell(cell);
            sc.Cells.CollectionChanged -= ScenarioCellsChanged;
        }

        private void ScenarioCellsChanged(object? sender, NotifyCollectionChangedEventArgs e)
        {
            if (e.NewItems != null)
                foreach (ScenarioVoltageCell c in e.NewItems) HookCell(c);
            if (e.OldItems != null)
                foreach (ScenarioVoltageCell c in e.OldItems) UnhookCell(c);
            RaiseChanged();
        }

        private void HookCell(ScenarioVoltageCell c)
        {
            c.PropertyChanged -= ItemChanged;
            c.PropertyChanged += ItemChanged;
        }

        private void UnhookCell(ScenarioVoltageCell c) => c.PropertyChanged -= ItemChanged;

        private void HookCollection<T>(ObservableCollection<T> coll) where T : INotifyPropertyChanged
        {
            foreach (var item in coll) Hook(item);
            coll.CollectionChanged += (_, e) =>
            {
                if (e.NewItems != null)
                    foreach (INotifyPropertyChanged x in e.NewItems) Hook(x);
                if (e.OldItems != null)
                    foreach (INotifyPropertyChanged x in e.OldItems) Unhook(x);
                // Adding / removing a component changes both the geometry and the tree.
                RaiseStructureChanged();
                RaiseChanged();
            };
        }

        private void Hook(INotifyPropertyChanged item)
        {
            item.PropertyChanged -= ItemChanged;
            item.PropertyChanged += ItemChanged;
        }

        private void Unhook(INotifyPropertyChanged item) => item.PropertyChanged -= ItemChanged;

        private void ItemChanged(object? sender, PropertyChangedEventArgs e)
        {
            // A winding rename changes the available parent names and scenario columns.
            if (sender is WindingViewModel && e.PropertyName == nameof(WindingViewModel.Name))
                SyncAvailableWindings();

            // A rename changes a tree node's label, and re-parenting a static ring moves it
            // between its parent winding and the top-level group; both alter the tree and so
            // need a structural refresh. Everything else is a pure value edit that only the
            // geometry cares about, so the case explorer is left untouched.
            if (e.PropertyName == nameof(ComponentViewModel.Name) ||
                (sender is StaticRingViewModel && e.PropertyName == nameof(StaticRingViewModel.ParentWinding)))
            {
                RaiseStructureChanged();
            }

            RaiseChanged();
        }

        partial void OnNameChanged(string value)
        {
            // The case name is the tree's root label.
            RaiseStructureChanged();
            RaiseChanged();
        }

        private void RaiseChanged() => Changed?.Invoke();

        private void RaiseStructureChanged() => StructureChanged?.Invoke();

        public ElectrostatCase ToModel()
        {
            // Rebuild voltages dictionary, preserving voltages and applying per-component edits.
            var voltages = new Dictionary<string, double>(Voltages);
            foreach (var w in Windings) voltages[w.Name] = w.Voltage;

            // A nested ring's metal voltage follows its parent winding; an independent ring
            // uses its own Voltage. (The solver re-derives this per scenario via
            // ElectrostatCase.EffectiveVoltages, but seeding the base map keeps a single
            // no-scenario build consistent too.)
            var windingByName = Windings.ToDictionary(w => w.Name, w => w.Voltage);
            foreach (var sr in StaticRings)
            {
                double metalV = sr.Voltage;
                if (sr.IsVoltageInherited && windingByName.TryGetValue(sr.ParentWinding, out var pv))
                    metalV = pv;
                voltages[sr.Name + "_Metal"] = metalV;
            }

            var scenarios = Scenarios.Count > 0
                ? Scenarios.Select(s => s.ToModel()).ToList()
                : null;

            return new ElectrostatCase(
                Name,
                Domain.ToModel(),
                Windings.Select(w => w.ToModel()).ToList(),
                Pressboards.Select(p => p.ToModel()).ToList(),
                AngleRings.Select(a => a.ToModel()).ToList(),
                StaticRings.Select(s => s.ToModel()).ToList(),
                voltages,
                scenarios);
        }
    }
}
