using System.Security.Cryptography;
using System.Text;
using InvoiceProcessor.Web.Infrastructure;
using Microsoft.Extensions.Options;

namespace InvoiceProcessor.Web.Services.Storage;

public class FileStorage(IOptions<AppOptions> options) : IFileStorage
{
    private readonly StorageOptions _storage = options.Value.Storage;

    public async Task<(string inboxPath, string storePath)> SaveIncomingPdfAsync(string fileName, byte[] content, CancellationToken cancellationToken)
    {
        var datePath = DateTime.UtcNow;
        var inboxDir = Path.Combine(_storage.InboxRoot, datePath.ToString("yyyy"), datePath.ToString("MM"), datePath.ToString("dd"));
        Directory.CreateDirectory(inboxDir);
        Directory.CreateDirectory(_storage.StoreRoot);

        var safeName = Path.GetFileName(fileName);
        var inboxPath = Path.Combine(inboxDir, safeName);
        await File.WriteAllBytesAsync(inboxPath, content, cancellationToken);

        var storePath = Path.Combine(_storage.StoreRoot, $"{Guid.NewGuid():N}.pdf");
        await File.WriteAllBytesAsync(storePath, content, cancellationToken);

        return (inboxPath, storePath);
    }

    public async Task<string> ReadTextAsync(string path, CancellationToken cancellationToken)
    {
        if (!File.Exists(path)) return string.Empty;
        return await File.ReadAllTextAsync(path, Encoding.UTF8, cancellationToken);
    }

    public string ComputeSha256(byte[] content)
    {
        var hash = SHA256.HashData(content);
        return Convert.ToHexString(hash);
    }
}
