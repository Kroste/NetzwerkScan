using System.Globalization;
using Avalonia.Data;
using Avalonia.Data.Converters;

namespace NetScanner.Converters;

/// <summary>
/// Bindet einen Enum-Wert an das <c>IsChecked</c> eines RadioButtons: liefert true,
/// wenn der gebundene Wert dem <c>ConverterParameter</c> entspricht. Beim Anhaken
/// setzt <see cref="ConvertBack"/> den Enum-Wert zurück; ein Abhaken wird ignoriert
/// (der Wert bleibt bis ein anderer RadioButton der Gruppe aktiv wird).
/// </summary>
public sealed class EnumMatchConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
        => string.Equals(value?.ToString(), parameter?.ToString(), StringComparison.Ordinal);

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        if (value is true && parameter is string name)
        {
            // targetType ist der Enum-Typ der Quell-Property.
            var enumType = Nullable.GetUnderlyingType(targetType) ?? targetType;
            if (enumType.IsEnum && Enum.TryParse(enumType, name, out var parsed))
                return parsed;
        }
        return BindingOperations.DoNothing;
    }
}
