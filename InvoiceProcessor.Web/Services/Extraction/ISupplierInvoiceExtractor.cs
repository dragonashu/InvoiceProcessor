using InvoiceProcessor.Web.Contracts;
using UglyToad.PdfPig;

namespace InvoiceProcessor.Web.Services.Extraction;

public interface ISupplierInvoiceExtractor
{
    string SupplierKey { get; }
    bool CanHandle(string rawText);
    CanonicalInvoice Extract(PdfDocument pdf, string rawText);
}
