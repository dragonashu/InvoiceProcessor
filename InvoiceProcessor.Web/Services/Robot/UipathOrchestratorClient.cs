using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using InvoiceProcessor.Web.Infrastructure;
using Microsoft.Extensions.Options;

namespace InvoiceProcessor.Web.Services.Robot;

public class UipathOrchestratorClient(HttpClient httpClient, IOptions<AppOptions> options, ILogger<UipathOrchestratorClient> logger) : IOrchestratorClient
{
    private readonly OrchestratorOptions _options = options.Value.Orchestrator;

    public async Task TriggerProcessAsync(string batchId, CancellationToken cancellationToken)
    {
        if (!_options.Enabled || string.IsNullOrWhiteSpace(_options.BaseUrl))
        {
            logger.LogInformation("UiPath Orchestrator disabled, skipping trigger for batch {BatchId}", batchId);
            return;
        }

        httpClient.BaseAddress = new Uri(_options.BaseUrl);
        if (!string.IsNullOrWhiteSpace(_options.ApiToken))
        {
            httpClient.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", _options.ApiToken);
        }

        var payload = new
        {
            startInfo = new
            {
                ReleaseKey = _options.ProcessKey,
                Strategy = "ModernJobsCount",
                JobsCount = 1,
                Source = "Manual",
                InputArguments = JsonSerializer.Serialize(new { batchId })
            }
        };

        var response = await httpClient.PostAsync("/odata/Jobs/UiPath.Server.Configuration.OData.StartJobs", new StringContent(JsonSerializer.Serialize(payload), Encoding.UTF8, "application/json"), cancellationToken);
        response.EnsureSuccessStatusCode();
    }
}
