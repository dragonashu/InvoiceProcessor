using InvoiceProcessor.Web.Background;
using InvoiceProcessor.Web.Data;
using InvoiceProcessor.Web.Infrastructure;
using InvoiceProcessor.Web.Services.Email;
using InvoiceProcessor.Web.Services.Extraction;
using InvoiceProcessor.Web.Services.Matching;
using InvoiceProcessor.Web.Services.Robot;
using InvoiceProcessor.Web.Services.Storage;
using Microsoft.EntityFrameworkCore;

var builder = WebApplication.CreateBuilder(args);

builder.Services.Configure<AppOptions>(builder.Configuration.GetSection("App"));
builder.Services.AddDbContext<AppDbContext>(o => o.UseSqlite(builder.Configuration.GetConnectionString("Default") ?? "Data Source=./data/invoice-processor.db"));

builder.Services.AddRazorPages();
builder.Services.AddControllers();
builder.Services.AddHttpClient<IOrchestratorClient, UipathOrchestratorClient>();

builder.Services.AddScoped<IEmailDispatcher, ImapEmailDispatcher>();
builder.Services.AddScoped<IFileStorage, FileStorage>();
builder.Services.AddScoped<IDocumentClassifier, RuleBasedDocumentClassifier>();
builder.Services.AddScoped<ICanonicalParser, StrategyCanonicalParser>();
builder.Services.AddScoped<IMatchingEngine, MatchingEngine>();
builder.Services.AddScoped<IExtractionPipeline, PdfExtractionPipeline>();
builder.Services.AddScoped<IPostingJobService, PostingJobService>();

builder.Services.AddHostedService<DispatcherWorker>();

var app = builder.Build();

Directory.CreateDirectory("./data");
using (var scope = app.Services.CreateScope())
{
    var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
    db.Database.Migrate();
}

app.UseStaticFiles();
app.UseRouting();
app.MapControllers();
app.MapRazorPages();
app.MapGet("/", () => Results.Redirect("/Inbox"));

app.Run();
