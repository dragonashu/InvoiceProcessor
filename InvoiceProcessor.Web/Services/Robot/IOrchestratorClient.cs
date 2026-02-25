namespace InvoiceProcessor.Web.Services.Robot;

public interface IOrchestratorClient
{
    Task TriggerProcessAsync(string batchId, CancellationToken cancellationToken);
}
