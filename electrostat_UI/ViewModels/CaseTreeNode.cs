using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;

namespace electrostat_UI.ViewModels
{
    /// <summary>
    /// Lightweight node used by the case-explorer TreeView.
    /// </summary>
    public sealed partial class CaseTreeNode : ObservableObject
    {
        public CaseTreeNode(string name, object? model = null, object? key = null)
        {
            Name = name;
            Model = model;
            Key = key ?? model ?? name;
            Children = new ObservableCollection<CaseTreeNode>();
        }

        public string Name { get; }

        /// <summary>Optional underlying model (e.g. a WindingBlock, StaticRing, ...).</summary>
        public object? Model { get; }

        /// <summary>
        /// Stable identity used to preserve expansion / selection across structural tree
        /// rebuilds. Model nodes key on their (reused) model instance; group nodes pass an
        /// explicit key so the changing "(count)" suffix in <see cref="Name"/> is ignored.
        /// </summary>
        public object Key { get; }

        public ObservableCollection<CaseTreeNode> Children { get; }

        /// <summary>
        /// Whether this node is expanded. Bound two-way to the <c>TreeViewItem</c> so the
        /// expansion survives a rebuild instead of collapsing back to the default state.
        /// </summary>
        [ObservableProperty]
        private bool _isExpanded;
    }
}
