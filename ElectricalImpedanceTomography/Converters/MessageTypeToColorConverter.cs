using System.Globalization;
using Utility.Classes.Application;

namespace ElectricalImpedanceTomography.Converters
{
    public class MessageTypeToColorConverter : IValueConverter
    {
        public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
        {
            if (value is WorkspaceMessageType type)
            {
                // Use app theme to ensure log messages are visible on both
                // light and dark backgrounds.  When no explicit colour is
                // defined ("Log" messages), we default to black for light
                // theme and white for dark theme.
                var isDark = Application.Current?.RequestedTheme == AppTheme.Dark;

                return type switch
                {
                    WorkspaceMessageType.Error => Colors.Red,
                    WorkspaceMessageType.Log => Colors.White,
                    WorkspaceMessageType.Warning => Color.FromArgb("#B8860B"),
                    WorkspaceMessageType.Loading => Colors.Green,
                    WorkspaceMessageType.Info => Colors.Blue,
                    _ => isDark ? Colors.White : Colors.Black
                };
            }
            return Colors.Black;
        }

        public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
            => throw new NotImplementedException();
    }
}
