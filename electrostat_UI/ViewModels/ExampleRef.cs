namespace electrostat_UI.ViewModels
{
    /// <summary>
    /// A bundled example case surfaced in the File ▸ Examples menu: a display
    /// <paramref name="Name"/> and the full <paramref name="Path"/> of its <c>.estat</c> file.
    /// </summary>
    /// <param name="Name">Display name shown in the menu.</param>
    /// <param name="Path">Full path to the example's <c>.estat</c> file.</param>
    public sealed record ExampleRef(string Name, string Path);
}
