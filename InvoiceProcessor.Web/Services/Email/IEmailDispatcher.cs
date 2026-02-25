namespace InvoiceProcessor.Web.Services.Email;

public interface IEmailDispatcher
{
    Task<int> PollAsync(CancellationToken cancellationToken);
}
