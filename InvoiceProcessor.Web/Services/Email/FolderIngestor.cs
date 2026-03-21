using InvoiceProcessor.Web.Data;
using InvoiceProcessor.Web.Enums;
using InvoiceProcessor.Web.Infrastructure;
using InvoiceProcessor.Web.Models;
using InvoiceProcessor.Web.Services.Storage;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace InvoiceProcessor.Web.Services.Email;

public class FolderIngestor(
    AppDbContext db,
    IFileStorage fileStorage,
    IOptions<AppOptions> options,
    ILogger<FolderIngestor> logger) : IEmailDispatcher
{
    private readonly string _sourceFolder = options.Value.Storage.SourceFolder;

    public async Task<int> PollAsync(CancellationToken cancellationToken)
    {
        if (!Directory.Exists(_sourceFolder))
        {
            logger.LogWarning("Source folder does not exist: {Folder}", _sourceFolder);
            return 0;
        }

        var pdfFiles = Directory.GetFiles(_sourceFolder, "*.pdf", SearchOption.AllDirectories);
        var created = 0;

        foreach (var filePath in pdfFiles)
        {
            try
            {
                var content = await File.ReadAllBytesAsync(filePath, cancellationToken);
                var hash = fileStorage.ComputeSha256(content);

                var exists = await db.Documents.AnyAsync(d => d.PdfHash == hash, cancellationToken);
                if (exists)
                    continue;

                var fileName = Path.GetFileName(filePath);
                var (_, storePath) = await fileStorage.SaveIncomingPdfAsync(fileName, content, cancellationToken);

                var document = new Document
                {
                    Source = "FOLDER",
                    Filename = fileName,
                    PdfHash = hash,
                    StoragePath = storePath,
                    Status = DocumentStatus.Received
                };

                db.Documents.Add(document);
                db.AuditEvents.Add(new AuditEvent
                {
                    DocumentId = document.Id,
                    EventType = "FOLDER_INGEST",
                    Message = $"Ingested from {filePath}"
                });

                await db.SaveChangesAsync(cancellationToken);
                created++;
                logger.LogInformation("Ingested PDF from folder: {FileName} → {DocumentId}", fileName, document.Id);
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Failed to ingest {FilePath}", filePath);
            }
        }

        return created;
    }
}
