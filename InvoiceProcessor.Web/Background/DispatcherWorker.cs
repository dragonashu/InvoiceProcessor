using InvoiceProcessor.Web.Services.Email;
using InvoiceProcessor.Web.Services.Extraction;

namespace InvoiceProcessor.Web.Background;

public class DispatcherWorker(
    IEmailDispatcher dispatcher,
    IExtractionPipeline pipeline,
    IConfiguration configuration,
    ILogger<DispatcherWorker> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var pollSeconds = configuration.GetValue<int?>("App:Email:PollSeconds") ?? 30;
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await dispatcher.PollAsync(stoppingToken);
                await pipeline.ProcessPendingAsync(stoppingToken);
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Worker loop failure");
            }

            await Task.Delay(TimeSpan.FromSeconds(pollSeconds), stoppingToken);
        }
    }
}
