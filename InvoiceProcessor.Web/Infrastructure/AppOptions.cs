namespace InvoiceProcessor.Web.Infrastructure;

public class AppOptions
{
    public EmailOptions Email { get; set; } = new();
    public StorageOptions Storage { get; set; } = new();
    public ExtractionOptions Extraction { get; set; } = new();
    public MatchingOptions Matching { get; set; } = new();
    public OrchestratorOptions Orchestrator { get; set; } = new();
}

public class EmailOptions
{
    public string Host { get; set; } = string.Empty;
    public int Port { get; set; } = 993;
    public bool UseSsl { get; set; } = true;
    public string Username { get; set; } = string.Empty;
    public string Password { get; set; } = string.Empty;
    public int PollSeconds { get; set; } = 30;
}

public class StorageOptions
{
    public string InboxRoot { get; set; } = "./data/inbox";
    public string StoreRoot { get; set; } = "./data/store";
    public string SourceFolder { get; set; } = "./samples";
    public string DviFolder { get; set; } = "./DVI";
    public string DviStoreRoot { get; set; } = "./data/dvi";
}

public class ExtractionOptions
{
    public int EmbeddedTextThreshold { get; set; } = 100;
    public decimal ValidationTolerance { get; set; } = 0.05m;
}

public class MatchingOptions
{
    public decimal MinConfidence { get; set; } = 0.75m;
}

public class OrchestratorOptions
{
    public string BaseUrl { get; set; } = string.Empty;
    public string? ApiToken { get; set; }
    public string ProcessKey { get; set; } = "WinMentorPoster";
    public bool Enabled { get; set; }
}
