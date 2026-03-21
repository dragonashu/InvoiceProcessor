using System.Globalization;

namespace InvoiceProcessor.Web.Services.Extraction;

/// <summary>
/// Parses numbers in European format where dot = thousands separator and comma = decimal separator.
/// Examples: "21.114,18" → 21114.18, "58,500" → 58.5, "1,110" → 1.110, "64,94" → 64.94
/// </summary>
public static class EuropeanNumberParser
{
    public static decimal? TryParse(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return null;

        var normalized = value.Trim().Replace(" ", string.Empty);

        // Strip dots (thousands separators), then replace comma with dot (decimal separator)
        normalized = normalized.Replace(".", string.Empty).Replace(",", ".");

        return decimal.TryParse(normalized, NumberStyles.Any, CultureInfo.InvariantCulture, out var result)
            ? result
            : null;
    }

    public static decimal Parse(string value)
    {
        return TryParse(value) ?? throw new FormatException($"Cannot parse European number: '{value}'");
    }
}
