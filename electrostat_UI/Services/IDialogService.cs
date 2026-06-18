using System.Threading.Tasks;

namespace electrostat_UI.Services
{
    /// <summary>Result of prompting the user to save pending changes.</summary>
    public enum SaveChangesResult
    {
        /// <summary>Save the changes, then continue the pending action.</summary>
        Save,

        /// <summary>Discard the changes and continue the pending action.</summary>
        Discard,

        /// <summary>Cancel the pending action and keep editing.</summary>
        Cancel,
    }

    /// <summary>
    /// Abstracts the file pickers and the "save changes?" prompt so the view model stays
    /// free of any window / <c>TopLevel</c> reference (and works at design time).
    /// </summary>
    public interface IDialogService
    {
        /// <summary>Prompt for an existing <c>.estat</c> file to open. Returns null if cancelled.</summary>
        Task<string?> OpenFileAsync();

        /// <summary>
        /// Prompt for a location to save an <c>.estat</c> file. Returns the chosen path, or
        /// null if cancelled.
        /// </summary>
        /// <param name="suggestedFileName">Initial file name to suggest in the dialog.</param>
        Task<string?> SaveFileAsync(string? suggestedFileName);

        /// <summary>Ask the user whether to save, discard, or cancel when changes are pending.</summary>
        Task<SaveChangesResult> ConfirmSaveChangesAsync();
    }
}
