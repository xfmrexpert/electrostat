using System;
using System.Globalization;
using Avalonia.Data.Converters;
using Avalonia.Media;

namespace electrostat_UI.ViewModels
{
    /// <summary>
    /// Maps a boolean "is governing" flag to a font weight so the worst-case scenario row
    /// stands out (Bold when true, Normal otherwise).
    /// </summary>
    public sealed class BoolToWeightConverter : IValueConverter
    {
        public static readonly BoolToWeightConverter Instance = new();

        public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
            => value is true ? FontWeight.Bold : FontWeight.Normal;

        public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
            => throw new NotSupportedException();
    }

    /// <summary>
    /// Maps a boolean "is governing" flag to a short marker shown in the envelope's
    /// "Worst?" column ("◀ worst" when true, empty otherwise).
    /// </summary>
    public sealed class BoolToWorstConverter : IValueConverter
    {
        public static readonly BoolToWorstConverter Instance = new();

        public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
            => value is true ? "◀ worst" : string.Empty;

        public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
            => throw new NotSupportedException();
    }
}
