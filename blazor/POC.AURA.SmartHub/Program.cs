using POC.AURA.SmartHub.Services;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddRazorComponents()
    .AddInteractiveServerComponents();

// Singleton: shared across all browser tabs, auto-connects to all tenants on startup
builder.Services.AddSingleton<PrintHubService>();
builder.Services.AddSingleton<BankHubService>();
builder.Services.AddHostedService<SmartHubStartupService>();

var app = builder.Build();

if (!app.Environment.IsDevelopment())
{
    app.UseExceptionHandler("/Error");
}

app.UseStaticFiles();
app.UseAntiforgery();

app.MapRazorComponents<POC.AURA.SmartHub.Components.App>()
    .AddInteractiveServerRenderMode();

app.Run();
