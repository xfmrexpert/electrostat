using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Avalonia.Controls;
using Avalonia.Platform.Storage;
using electrostat.IO;
using electrostat_UI.Views.Dialogs;

namespace electrostat_UI.Services
{
    /// <summary>
    /// <see cref="IDialogService"/> backed by a concrete <see cref="Window"/>: it uses the
    /// window's <see cref="IStorageProvider"/> for file pickers and shows modal dialogs as
    /// children of that window.
    /// </summary>
    public sealed class DialogService : IDialogService
    {
        private readonly Window _owner;

        private static readonly FilePickerFileType EstatFileType = new("Electrostat Case")
        {
            Patterns = new[] { "*" + TransformerSerializer.FileExtension },
        };

        public DialogService(Window owner)
        {
            _owner = owner ?? throw new ArgumentNullException(nameof(owner));
        }

        public async Task<string?> OpenFileAsync()
        {
            var files = await _owner.StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
            {
                Title = "Open Case",
                AllowMultiple = false,
                FileTypeFilter = new[] { EstatFileType },
            });

            if (files.Count == 0)
                return null;

            return files[0].TryGetLocalPath();
        }

        public async Task<string?> SaveFileAsync(string? suggestedFileName)
        {
            var file = await _owner.StorageProvider.SaveFilePickerAsync(new FilePickerSaveOptions
            {
                Title = "Save Case",
                SuggestedFileName = suggestedFileName,
                DefaultExtension = TransformerSerializer.FileExtension.TrimStart('.'),
                ShowOverwritePrompt = true,
                FileTypeChoices = new[] { EstatFileType },
            });

            return file?.TryGetLocalPath();
        }

        public Task<SaveChangesResult> ConfirmSaveChangesAsync()
            => SaveChangesDialog.ShowAsync(_owner);
    }
}
