using System.Globalization;

namespace ElectricalImpedanceTomography.Converters
{
    public class TypeCheckConverter : IValueConverter
    {
        public Type? TargetType { get; set; }
        public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture) => value?.GetType() == TargetType;
        public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) => throw new NotImplementedException();
    }
}
