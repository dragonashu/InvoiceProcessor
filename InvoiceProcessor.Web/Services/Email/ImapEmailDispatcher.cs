using InvoiceProcessor.Web.Data;
using InvoiceProcessor.Web.Enums;
using InvoiceProcessor.Web.Models;
using InvoiceProcessor.Web.Services.Storage;
using MailKit;
using MailKit.Net.Imap;
using MailKit.Search;
using Microsoft.EntityFrameworkCore;

namespace InvoiceProcessor.Web.Services.Email;

public class ImapEmailDispatcher(AppDbContext db, IFileStorage storage, IConfiguration configuration, ILogger<ImapEmailDispatcher> logger) : IEmailDispatcher
{
    public async Task<int> PollAsync(CancellationToken cancellationToken)
    {
        var email = configuration.GetSection("App:Email");
        if (string.IsNullOrWhiteSpace(email["Host"]) || string.IsNullOrWhiteSpace(email["Username"]))
        {
            logger.LogInformation("Email dispatcher disabled. Missing host/username.");
            return 0;
        }

        using var client = new ImapClient();
        await client.ConnectAsync(email["Host"], email.GetValue<int>("Port"), email.GetValue<bool>("UseSsl"), cancellationToken);
        await client.AuthenticateAsync(email["Username"], email["Password"], cancellationToken);
        var inbox = client.Inbox;
        await inbox.OpenAsync(FolderAccess.ReadWrite, cancellationToken);

        var uids = await inbox.SearchAsync(SearchQuery.NotSeen, cancellationToken);
        var created = 0;

        foreach (var uid in uids)
        {
            var message = await inbox.GetMessageAsync(uid, cancellationToken);
            foreach (var attachment in message.Attachments.OfType<MimeKit.MimePart>().Where(x => x.FileName.EndsWith(".pdf", StringComparison.OrdinalIgnoreCase)))
            {
                await using var ms = new MemoryStream();
                await attachment.Content.DecodeToAsync(ms, cancellationToken);
                var content = ms.ToArray();
                var hash = storage.ComputeSha256(content);

                if (await db.Documents.AnyAsync(d => d.PdfHash == hash, cancellationToken))
                {
                    db.AuditEvents.Add(new AuditEvent { EventType = "DUPLICATE", Message = $"Duplicate attachment skipped: {attachment.FileName}", PayloadJson = $"{{\"hash\":\"{hash}\"}}" });
                    continue;
                }

                var (_, storePath) = await storage.SaveIncomingPdfAsync(attachment.FileName, content, cancellationToken);
                db.Documents.Add(new Document
                {
                    Filename = attachment.FileName,
                    EmailFrom = message.From.ToString(),
                    EmailSubject = message.Subject,
                    PdfHash = hash,
                    StoragePath = storePath,
                    Status = DocumentStatus.Received
                });
                created++;
            }

            await inbox.AddFlagsAsync(uid, MessageFlags.Seen, true, cancellationToken);
        }

        await db.SaveChangesAsync(cancellationToken);
        await client.DisconnectAsync(true, cancellationToken);
        return created;
    }
}
