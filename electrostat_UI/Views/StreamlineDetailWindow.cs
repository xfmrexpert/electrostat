using Avalonia.Controls;
using electrostat;

namespace electrostat_UI.Views
{
    /// <summary>
    /// A small pop-up window that plots stress (|E|) and margin along a single
    /// streamline. Opened from <see cref="ResultsView"/> when the user double-clicks
    /// a streamline in the results plot.
    /// </summary>
    public sealed class StreamlineDetailWindow : Window
    {
        public StreamlineDetailWindow()
            : this(null, 0)
        {
        }

        public StreamlineDetailWindow(StreamlineWithMargin? streamline, int number)
        {
            Width = 720;
            Height = 560;
            Title = $"Streamline #{number} — Stress & Margin";
            WindowStartupLocation = WindowStartupLocation.CenterOwner;

            Content = new StreamlineStressPlotView
            {
                Streamline = streamline,
                StreamlineNumber = number,
            };
        }
    }
}
