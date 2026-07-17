using System.Threading.Tasks;
using Avalonia.Controls;
using Avalonia.Interactivity;
using electrostat_UI.Services;

namespace electrostat_UI.Views.Dialogs
{
    /// <summary>
    /// Modal Save / Don't Save / Cancel prompt shown before discarding unsaved changes.
    /// Returns the chosen <see cref="SaveChangesResult"/> from <see cref="ShowAsync"/>.
    /// </summary>
    public partial class SaveChangesDialog : Window
    {
        public SaveChangesDialog()
        {
            InitializeComponent();

            SaveButton.Click += OnSave;
            DiscardButton.Click += OnDiscard;
            CancelButton.Click += OnCancel;
        }

        /// <summary>Show the dialog modally over <paramref name="owner"/> and await the result.</summary>
        public static Task<SaveChangesResult> ShowAsync(Window owner, string documentName)
        {
            var dialog = new SaveChangesDialog();
            dialog.MessageText.Text = $"Do you want to save changes to “{documentName}”?";
            return dialog.ShowDialog<SaveChangesResult>(owner);
        }

        private void OnSave(object? sender, RoutedEventArgs e) => Close(SaveChangesResult.Save);

        private void OnDiscard(object? sender, RoutedEventArgs e) => Close(SaveChangesResult.Discard);

        private void OnCancel(object? sender, RoutedEventArgs e) => Close(SaveChangesResult.Cancel);
    }
}
