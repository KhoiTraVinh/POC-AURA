using POC.AURA.SmartHub.Services;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddRazorComponents()
    .AddInteractiveServerComponents();

// Scoped: one instance per Blazor circuit (browser tab)
builder.Services.AddScoped<PrintHubService>();
builder.Services.AddScoped<BankHubService>();

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
