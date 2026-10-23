using System.Globalization;
using System.Text.Json;
using System.Windows.Data;

namespace AM.Desktop.Views;

/// <summary>将对象转换为格式化的 JSON 字符串。</summary>
public sealed class ObjectToJsonConverter : IValueConverter
{
    private static readonly JsonSerializerOptions Options = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
    };

    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        if (value is null)
        {
            return "";
        }

        try
        {
            return JsonSerializer.Serialize(value, Options);
        }
        catch
        {
            return value.ToString() ?? "";
        }
    }

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}
