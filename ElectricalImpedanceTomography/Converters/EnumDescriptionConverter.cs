using System.ComponentModel;
using System.Globalization;
using System.Reflection;
using System.Text.RegularExpressions;

namespace ElectricalImpedanceTomography.Converters
{
    public class EnumDescriptionConverter : IValueConverter
    {
        private static readonly Regex PascalBreak =
            new Regex(@"(?<!^)([A-Z][a-z]|(?<=[a-z])[A-Z])",
                      RegexOptions.Compiled);

        public object Convert(object? value, Type targetType,
                              object? parameter, CultureInfo culture)
        {
            if (value == null) return null!;

            // 1) check for [Description(...)]
            var field = value.GetType().GetField(value.ToString()!);
            var attr = field?.GetCustomAttribute<DescriptionAttribute>();
            if (attr != null) return attr.Description;

            // 2) fallback: split PascalCase  + turn '_' into blanks
            string text = value.ToString()!.Replace('_', ' ');
            return PascalBreak.Replace(text, " $1");
        }

        public object ConvertBack(object? value, Type targetType,
                                  object? parameter, CultureInfo culture)
            => value!;
    }
}
