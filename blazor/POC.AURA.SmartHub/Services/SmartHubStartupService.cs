namespace POC.AURA.SmartHub.Services;

public class SmartHubStartupService : IHostedService
{
    private readonly PrintHubService _printHub;
    private readonly BankHubService _bankHub;
    private readonly IConfiguration _config;
    private readonly ILogger<SmartHubStartupService> _logger;

    public SmartHubStartupService(
        PrintHubService printHub,
        BankHubService bankHub,
        IConfiguration config,
        ILogger<SmartHubStartupService> logger)
    {
        _printHub = printHub;
        _bankHub = bankHub;
        _config = config;
        _logger = logger;
    }

    public Task StartAsync(CancellationToken cancellationToken)
    {
        // Run connection in background so app startup is not blocked
        _ = ConnectWithRetryAsync(cancellationToken);
        return Task.CompletedTask;
    }

    private async Task ConnectWithRetryAsync(CancellationToken cancellationToken)
    {
        var tenants = _config.GetSection("Tenants").Get<string[]>() ?? ["TenantA", "TenantB"];
        var backendUrl = _config["Backend:Url"] ?? "http://backend:8080";

        _logger.LogInformation("SmartHub waiting for backend at {Url}...", backendUrl);

        // Wait until backend health endpoint responds
        using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(3) };
        var retries = 0;
        while (!cancellationToken.IsCancellationRequested)
        {
            try
            {
                var resp = await http.GetAsync($"{backendUrl}/health", cancellationToken);
                if (resp.IsSuccessStatusCode) break;
            }
            catch
            {
                // backend not ready yet
            }

            retries++;
            var delay = retries <= 5 ? 3000 : 10000;
            _logger.LogInformation("Backend not ready, retrying in {Delay}ms (attempt {Retry})...", delay, retries);
            await Task.Delay(delay, cancellationToken);
        }

        if (cancellationToken.IsCancellationRequested) return;

        _logger.LogInformation("Backend ready. Connecting to {Count} tenants: {Tenants}",
            tenants.Length, string.Join(", ", tenants));

        await Task.WhenAll(
            _printHub.ConnectAllAsync(tenants),
            _bankHub.ConnectAllAsync(tenants)
        );
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}
