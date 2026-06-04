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

        public Dictionary<string, double> WallVoltages { get; }

        /// <summary>Raised whenever the case changes structurally or any field mutates.</summary>
        public event Action? Changed;

        public ElectrostatCaseViewModel(ElectrostatCase model)
        {
            _name = model.Name;
            Domain = new DomainViewModel(model.Domain);
            WallVoltages = new Dictionary<string, double>(model.Voltages);

            foreach (var w in model.Windings)
                Windings.Add(new WindingViewModel(w, GetVoltage(model.Voltages, w.Name)));

            foreach (var pb in model.Pressboards)
                Pressboards.Add(new PressboardViewModel(pb));

            foreach (var ar in model.AngleRings)
                AngleRings.Add(new AngleRingViewModel(ar));

            foreach (var sr in model.StaticRings)
                StaticRings.Add(new StaticRingViewModel(sr, GetVoltage(model.Voltages, sr.Name + "_Metal")));

            Hook(Domain);
            HookCollection(Windings);
            HookCollection(Pressboards);
            HookCollection(AngleRings);
            HookCollection(StaticRings);
        }

        private static double GetVoltage(IDictionary<string, double> v, string name)
            => v.TryGetValue(name, out var x) ? x : 0.0;

        private void HookCollection<T>(ObservableCollection<T> coll) where T : INotifyPropertyChanged
        {
            foreach (var item in coll) Hook(item);
            coll.CollectionChanged += (_, e) =>
            {
                if (e.NewItems != null)
                    foreach (INotifyPropertyChanged x in e.NewItems) Hook(x);
                if (e.OldItems != null)
                    foreach (INotifyPropertyChanged x in e.OldItems) Unhook(x);
                RaiseChanged();
            };
        }

        private void Hook(INotifyPropertyChanged item)
        {
            item.PropertyChanged -= ItemChanged;
            item.PropertyChanged += ItemChanged;
        }

        private void Unhook(INotifyPropertyChanged item) => item.PropertyChanged -= ItemChanged;

        private void ItemChanged(object? sender, PropertyChangedEventArgs e) => RaiseChanged();

        partial void OnNameChanged(string value) => RaiseChanged();

        private void RaiseChanged() => Changed?.Invoke();

        public ElectrostatCase ToModel()
        {
            // Rebuild voltages dictionary, preserving wall voltages and applying per-component edits.
            var voltages = new Dictionary<string, double>(WallVoltages);
            foreach (var w in Windings) voltages[w.Name] = w.Voltage;
            foreach (var sr in StaticRings) voltages[sr.Name + "_Metal"] = sr.Voltage;

            return new ElectrostatCase(
                Name,
                Domain.ToModel(),
                Windings.Select(w => w.ToModel()).ToList(),
                Pressboards.Select(p => p.ToModel()).ToList(),
                AngleRings.Select(a => a.ToModel()).ToList(),
                StaticRings.Select(s => s.ToModel()).ToList(),
                voltages);
        }
    }
}
