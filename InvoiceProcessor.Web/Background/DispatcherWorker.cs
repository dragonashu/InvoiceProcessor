using InvoiceProcessor.Web.Services.Email;
using InvoiceProcessor.Web.Services.Extraction;

namespace InvoiceProcessor.Web.Background;

public class DispatcherWorker(
    IServiceScopeFactory scopeFactory,
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
                using var scope = scopeFactory.CreateScope();
                var dispatcher = scope.ServiceProvider.GetRequiredService<IEmailDispatcher>();
                var pipeline = scope.ServiceProvider.GetRequiredService<IExtractionPipeline>();
                var dviScanner = scope.ServiceProvider.GetRequiredService<IDviFolderScanner>();

                var ingested = await dispatcher.PollAsync(stoppingToken);
                if (ingested > 0)
                    logger.LogInformation("Ingested {Count} new document(s)", ingested);

                await pipeline.ProcessPendingAsync(stoppingToken);

                var dviAdded = await dviScanner.ScanAsync(stoppingToken);
                if (dviAdded > 0)
                    logger.LogInformation("Ingested {Count} new DVI file(s)", dviAdded);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Worker loop failure");
            }

            await Task.Delay(TimeSpan.FromSeconds(pollSeconds), stoppingToken);
        }
    }
}
