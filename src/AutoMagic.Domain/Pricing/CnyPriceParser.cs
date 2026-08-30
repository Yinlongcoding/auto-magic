using System.Globalization;
using System.Text.RegularExpressions;

namespace AutoMagic.Domain.Pricing;

public static partial class CnyPriceParser
{
    [GeneratedRegex(@"\d[\d,]*(?:\.\d+)?", RegexOptions.CultureInvariant)]
    private static partial Regex FirstNumberPattern();

    public static bool TryParseMinimum(string? value, out decimal price)
    {
        price = 0;
        if (string.IsNullOrWhiteSpace(value))
        {
            return false;
        }

        var match = FirstNumberPattern().Match(value);
        if (!match.Success)
        {
            return false;
        }

        var token = NormalizeNumber(match.Value);
        return decimal.TryParse(
            token,
            NumberStyles.AllowDecimalPoint,
            CultureInfo.InvariantCulture,
            out price);
    }

    private static string NormalizeNumber(string token)
    {
        if (token.Contains('.'))
        {
            return token.Replace(",", string.Empty, StringComparison.Ordinal);
        }

        var commaIndex = token.LastIndexOf(',');
        if (commaIndex < 0)
        {
            return token;
        }

        var decimalDigits = token.Length - commaIndex - 1;
        return decimalDigits is 1 or 2
            ? token[..commaIndex] + "." + token[(commaIndex + 1)..]
            : token.Replace(",", string.Empty, StringComparison.Ordinal);
    }
}
