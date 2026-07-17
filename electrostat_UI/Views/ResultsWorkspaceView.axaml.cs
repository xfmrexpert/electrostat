using Avalonia.Controls;
using Avalonia.Interactivity;

namespace electrostat_UI.Views
{
    public partial class ResultsWorkspaceView : UserControl
    {
        public ResultsWorkspaceView()
        {
            InitializeComponent();
        }

        private void FitFieldPlot(object? sender, RoutedEventArgs e) => FieldPlot.ResetView();

        private void ZoomFieldPlotIn(object? sender, RoutedEventArgs e) => FieldPlot.ZoomIn();

        private void ZoomFieldPlotOut(object? sender, RoutedEventArgs e) => FieldPlot.ZoomOut();
    }
}
