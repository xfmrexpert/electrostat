using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Data.Core;
using Avalonia.Data.Core.Plugins;
using Avalonia.Markup.Xaml;
using electrostat_UI.Services;
using electrostat_UI.ViewModels;
using electrostat_UI.Views;
using System.Linq;

namespace electrostat_UI
{
    public partial class App : Application
    {
        public override void Initialize()
        {
            AvaloniaXamlLoader.Load(this);
        }

        public override void OnFrameworkInitializationCompleted()
        {
            if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
            {
                var window = new MainWindow();
                var dialogService = new DialogService(window);
                var viewModel = new MainWindowViewModel(dialogService)
                {
                    RequestClose = () => window.Close(),
                };

                window.DataContext = viewModel;
                desktop.MainWindow = window;
            }

            base.OnFrameworkInitializationCompleted();
        }
    }
}