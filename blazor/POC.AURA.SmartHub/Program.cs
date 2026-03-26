using Microsoft.AspNetCore.Components.Authorization;
using Microsoft.AspNetCore.Components.Server.ProtectedBrowserStorage;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;
using POC.AURA.SmartHub.Data;
using POC.AURA.SmartHub.Server.Hubs;
using POC.AURA.SmartHub.Server.Services;
using POC.AURA.SmartHub.Server.Workers;
using POC.AURA.SmartHub.Service.Auth;
using POC.AURA.SmartHub.Service.Events;
using POC.AURA.SmartHub.Service.Scheduling;
using POC.AURA.SmartHub.UI.Auth;
using POC.AURA.SmartHub.UI.Controllers;
using POC.AURA.SmartHub.UI.Middleware;
using POC.AURA.SmartHub.UI.Services;

var builder = WebApplication.CreateBuilder(args);

// ── Blazor / MVC ──────────────────────────────────────────────────────────
builder.Services.AddRazorComponents()
    .AddInteractiveServerComponents();
builder.Services.AddControllers();
builder.Services.AddSignalR();

// ── Auth (Blazor) ─────────────────────────────────────────────────────────
builder.Services.AddAuthorizationCore();
builder.Services.AddCascadingAuthenticationState();
builder.Services.AddScoped<ProtectedSessionStorage>();
builder.Services.AddScoped<AuthenticationStateProvider, CustomAuthenticationStateProvider>();

// ── MemoryCache (used by PKCE state storage) ──────────────────────────────
builder.Services.AddMemoryCache();

// ── Database (UBS.Eclipse.SmartHub.Data) ─────────────────────────────────
var dbPath = Path.Combine(
    Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
    "EclipseSmartHub", "smarthub.db");
Directory.CreateDirectory(Path.GetDirectoryName(dbPath)!);

builder.Services.AddDbContextFactory<SmartHubDbContext>(opts =>
    opts.UseSqlite($"Data Source={dbPath}"));

builder.Services.AddScoped<IServerConnectionRepository, ServerConnectionRepository>();

// ── Service layer (UBS.Eclipse.SmartHub.Service) ─────────────────────────
builder.Services.AddSingleton<IConnectionEventService, ConnectionEventService>();
builder.Services.AddScoped<ITokenSchedulerService, TokenSchedulerService>();
builder.Services.AddScoped<IClientAuthenticationService, ClientAuthenticationService>();

// ── Server layer (UBS.Eclipse.SmartHub.Server) ────────────────────────────
builder.Services.AddSingleton<IPrintService, PrintService>();
builder.Services.AddSingleton<IEftPosService, EftPosService>();

// HubConnectionWorker: Singleton (manages long-lived HubConnections to Aura servers)
builder.Services.AddSingleton<HubConnectionWorker>();
builder.Services.AddHostedService(sp => sp.GetRequiredService<HubConnectionWorker>());

builder.Services.AddScoped<ServerConnectionService>();
builder.Services.AddScoped<IServerConnectionService, ServerConnectionService>();

// ── UI layer (UBS.Eclipse.SmartHub.UI) ───────────────────────────────────
builder.Services.AddScoped<SignalRService>();

// ── CORS (AllowAnyOrigin + AllowCredentials via dynamic policy) ───────────
builder.Services.AddCors(o => o.AddDefaultPolicy(p =>
    p.SetIsOriginAllowed(_ => true).AllowAnyMethod().AllowAnyHeader().AllowCredentials()));

// ─────────────────────────────────────────────────────────────────────────
var app = builder.Build();

// ── DB: ensure schema exists ──────────────────────────────────────────────
using (var scope = app.Services.CreateScope())
{
    var db = scope.ServiceProvider.GetRequiredService<SmartHubDbContext>();
    db.Database.EnsureCreated();
}

if (!app.Environment.IsDevelopment())
    app.UseExceptionHandler("/Error");

// DecryptQueryStringMiddleware runs BEFORE route matching (per spec)
app.UseDecryptQueryString();

app.UseCors();
app.UseStaticFiles();
app.UseAntiforgery();

// BlazorConnectionHub at /clientServiceHub (port 6758 in production)
app.MapHub<BlazorConnectionHub>("/clientServiceHub");
app.MapControllers();

app.MapRazorComponents<POC.AURA.SmartHub.Components.App>()
    .AddInteractiveServerRenderMode();

app.Run();
