using InvoiceProcessor.Web.Contracts;
using InvoiceProcessor.Web.Infrastructure;
using Microsoft.Extensions.Options;

namespace InvoiceProcessor.Web.Services.Extraction;

public interface IInvoiceValidator
{
    ValidationResult Validate(CanonicalInvoice invoice);
}

public record ValidationResult(bool IsValid, decimal LineSum, decimal GrossTotal, decimal Difference, string? Reason);

public class InvoiceValidator(IOptions<AppOptions> options) : IInvoiceValidator
{
    public ValidationResult Validate(CanonicalInvoice invoice)
    {
        var lineSum = invoice.Lines.Sum(l => l.LineTotal);
        var grossTotal = invoice.GrossTotal ?? 0m;
        // Line totals are net amounts, so compare them against the net total.
        // The gross total includes VAT and never matches for invoices that
        // carry a VAT line (e.g. Romanian-VAT invoices). Fall back to gross
        // only when the extractor did not capture a net total.
        var compareTotal = invoice.NetTotal ?? grossTotal;
        var tolerance = options.Value.Extraction.ValidationTolerance;
        var maxDiff = Math.Max(compareTotal * tolerance, 0.50m);
        var difference = Math.Abs(lineSum - compareTotal);

        if (compareTotal == 0m)
            return new ValidationResult(false, lineSum, grossTotal, difference, "Total factura lipseste");

        if (difference > maxDiff)
            return new ValidationResult(false, lineSum, grossTotal, difference,
                $"Suma linii ({lineSum:N2}) difera de total ({compareTotal:N2}) cu {difference:N2}");

        return new ValidationResult(true, lineSum, grossTotal, difference, null);
    }
}
