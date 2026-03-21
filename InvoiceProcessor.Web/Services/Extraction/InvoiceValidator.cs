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
        var tolerance = options.Value.Extraction.ValidationTolerance;
        var maxDiff = Math.Max(grossTotal * tolerance, 0.50m);
        var difference = Math.Abs(lineSum - grossTotal);

        if (grossTotal == 0m)
            return new ValidationResult(false, lineSum, grossTotal, difference, "Total brut lipseste");

        if (difference > maxDiff)
            return new ValidationResult(false, lineSum, grossTotal, difference,
                $"Suma linii ({lineSum:N2}) difera de total ({grossTotal:N2}) cu {difference:N2}");

        return new ValidationResult(true, lineSum, grossTotal, difference, null);
    }
}
