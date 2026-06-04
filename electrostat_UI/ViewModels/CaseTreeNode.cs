using System.Collections.ObjectModel;

namespace electrostat_UI.ViewModels
{
    /// <summary>
    /// Lightweight node used by the case-explorer TreeView.
    /// </summary>
    public sealed class CaseTreeNode
    {
        public CaseTreeNode(string name, object? model = null)
        {
            Name = name;
            Model = model;
            Children = new ObservableCollection<CaseTreeNode>();
        }

        public string Name { get; }

        /// <summary>Optional underlying model (e.g. a WindingBlock, StaticRing, ...).</summary>
        public object? Model { get; }

        public ObservableCollection<CaseTreeNode> Children { get; }
    }
}
