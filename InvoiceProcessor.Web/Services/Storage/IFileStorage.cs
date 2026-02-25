namespace InvoiceProcessor.Web.Services.Storage;

public interface IFileStorage
{
    Task<(string inboxPath, string storePath)> SaveIncomingPdfAsync(string fileName, byte[] content, CancellationToken cancellationToken);
    Task<string> ReadTextAsync(string path, CancellationToken cancellationToken);
    string ComputeSha256(byte[] content);
}
