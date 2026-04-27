namespace InvoiceProcessor.Web.Services.Extraction;

public static class InvoiceRefMatcher
{
    /// True when the given invoice number and DVI invoice reference point at the same document,
    /// tolerating series/separator and zero-padding differences
    /// (e.g. Yildiz invoice "YCE2026000000101" ↔ DVI ref "YCE2026/101").
    public static bool Matches(string? invoiceNo, string? dviRef)
    {
        if (string.IsNullOrWhiteSpace(invoiceNo) || string.IsNullOrWhiteSpace(dviRef)) return false;
        var invN = invoiceNo.Trim().ToUpperInvariant();
        var parts = dviRef.Trim().ToUpperInvariant()
            .Split(['/', '-', ' ', '\\'], StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length == 0) return false;

        var series = parts[0];
        var number = parts[^1].TrimStart('0');
        if (number.Length == 0) number = "0";

        if (!invN.StartsWith(series)) return false;
        var tail = invN[series.Length..].TrimStart('0');
        if (tail.Length == 0) tail = "0";
        return tail == number;
    }
}
