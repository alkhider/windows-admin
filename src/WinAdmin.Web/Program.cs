using WinAdmin.Core.Services;
using WinAdmin.Web;
using WinAdmin.Web.Components;

PrivilegeEnablerBootstrap();

var contentRoot = ResolveContentRoot();
Directory.SetCurrentDirectory(contentRoot);

var builder = WebApplication.CreateBuilder(new WebApplicationOptions
{
    Args = args,
    ContentRootPath = contentRoot,
    ApplicationName = "WinAdmin.Web"
});

builder.Host.UseWindowsService();

var urls = Environment.GetEnvironmentVariable("WINADMIN_URLS")
           ?? builder.Configuration["WinAdmin:Urls"]
           ?? "http://127.0.0.1:5077";
builder.WebHost.UseUrls(urls);

builder.Services.AddRazorComponents()
    .AddInteractiveServerComponents();

builder.Services.AddHttpContextAccessor();
builder.Services.AddScoped<PortalAuthService>();
builder.Services.AddSingleton<HealthService>();
builder.Services.AddSingleton<WindowsUpdateService>();
builder.Services.AddSingleton<AppService>();
builder.Services.AddSingleton<SearchIndexService>();
builder.Services.AddSingleton<FolderBrowseService>();
builder.Services.AddSingleton<LocationMemoryService>();
builder.Services.AddSingleton<StorageService>();
builder.Services.AddSingleton<StorageSenseService>();
builder.Services.AddSingleton<SystemTaskService>();
builder.Services.AddSingleton<ProcessService>();
builder.Services.AddSingleton<EventLogService>();
builder.Services.AddSingleton<ScheduledTaskService>();
builder.Services.AddSingleton<FirewallService>();
builder.Services.AddSingleton<CertificateService>();
builder.Services.AddSingleton<DeviceService>();
builder.Services.AddSingleton<TransferJobService>();
builder.Services.AddSingleton<LiveMetricsService>();
builder.Services.AddSingleton<WindowsClockService>();
builder.Services.AddSingleton<JobService>();
builder.Services.AddSingleton<TelemetryStore>();
builder.Services.AddSingleton<RecommendationService>();
builder.Services.AddSingleton<CheckFixService>();
builder.Services.AddHostedService<PrivilegedHostSetup>();
builder.Services.AddHostedService<RecommendationSampler>();

var app = builder.Build();

if (!app.Environment.IsDevelopment())
{
    app.UseExceptionHandler("/Error", createScopeForErrors: true);
}

app.UseAntiforgery();
app.MapStaticAssets();
app.MapRazorComponents<App>()
    .AddInteractiveServerRenderMode();

app.MapGet("/api/health/report.html", (HealthService health) =>
{
    if (!PortalAuthService.ProcessUnlocked)
    {
        return Results.Unauthorized();
    }

    return Results.Content(HealthReportFormatter.ToHtml(health.GetReport()), "text/html; charset=utf-8");
});

app.MapGet("/api/health/report.json", (HealthService health) =>
{
    if (!PortalAuthService.ProcessUnlocked)
    {
        return Results.Unauthorized();
    }

    return Results.Text(HealthReportFormatter.ToJson(health.GetReport()), "application/json");
});

app.Run();

static void PrivilegeEnablerBootstrap()
{
    WinAdmin.Core.Infrastructure.PrivilegeEnabler.EnableAll();
}

static string ResolveContentRoot()
{
    var baseDir = AppContext.BaseDirectory;
    if (File.Exists(Path.Combine(baseDir, "wwwroot", "app.css")))
    {
        return baseDir;
    }

    var cwd = Directory.GetCurrentDirectory();
    if (File.Exists(Path.Combine(cwd, "wwwroot", "app.css")))
    {
        return cwd;
    }

    return baseDir;
}

internal sealed class PrivilegedHostSetup : IHostedService
{
    public Task StartAsync(CancellationToken cancellationToken)
    {
        WinAdmin.Core.Infrastructure.PrivilegeEnabler.EnableAll();
        _ = PortalServiceManager.TryRegisterCurrentHostAsync();
        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}

internal sealed class RecommendationSampler(RecommendationService recs) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                recs.CaptureSample();
            }
            catch
            {
                // keep sampling
            }

            try
            {
                await Task.Delay(TimeSpan.FromSeconds(30), stoppingToken);
            }
            catch (TaskCanceledException)
            {
                break;
            }
        }
    }
}
