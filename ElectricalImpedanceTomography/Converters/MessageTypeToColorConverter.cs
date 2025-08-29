using System;
using System.Globalization;
using Microsoft.Maui.Controls;
using Microsoft.Maui.Graphics;
using Utility.Classes.Application;

namespace ElectricalImpedanceTomography.Converters
{
    public class MessageTypeToColorConverter : IValueConverter
    {
        public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
        {
            if (value is WorkspaceMessageType type)
            {
                return type switch
                {
                    WorkspaceMessageType.Error => Colors.Red,
                    WorkspaceMessageType.Warning => Color.FromArgb("#B8860B"),
                    WorkspaceMessageType.Loading => Colors.Green,
                    _ => Colors.White
                };
            }
            return Colors.White;
        }

        public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
            => throw new NotImplementedException();
    }
}
