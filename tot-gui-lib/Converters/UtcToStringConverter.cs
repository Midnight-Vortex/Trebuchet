using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using Avalonia.Data.Converters;

namespace tot_gui_lib.Converters
{
    public sealed class UtcToStringConverter : IValueConverter
    {
        public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
        {
            if (value is DateTime dateTime)
                return dateTime.ToLocalTime().ToString("HH:mm:ss");
            throw new ArgumentException("Value must be a DateTime.", nameof(value));
        }

        public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        {
            throw new InvalidOperationException("Cannot convert back.");
        }
    }
}