using Avalonia.Controls;
using Avalonia.Interactivity;
using electrostat_UI.ViewModels;

namespace electrostat_UI.Views
{
    public partial class MainWindow : Window
    {
        // Set once the unsaved-changes guard has run and approved the close, so the second
        // (programmatic) Close() call is allowed straight through.
        private bool _forceClose;

        public MainWindow()
        {
            InitializeComponent();
        }

        private void FitGeometry(object? sender, RoutedEventArgs e) => GeometryCanvas.ResetView();

        private void ZoomGeometryIn(object? sender, RoutedEventArgs e) => GeometryCanvas.ZoomIn();

        private void ZoomGeometryOut(object? sender, RoutedEventArgs e) => GeometryCanvas.ZoomOut();

        protected override async void OnClosing(WindowClosingEventArgs e)
        {
            if (!_forceClose && DataContext is MainWindowViewModel vm)
            {
                // Defer the close until the prompt resolves; re-issue it on approval.
                e.Cancel = true;
                if (await vm.PromptSaveIfDirtyAsync())
                {
                    _forceClose = true;
                    Close();
                }
                return;
            }

            base.OnClosing(e);
        }
    }
}