using System.IdentityModel.Tokens.Jwt;
using System.Text.Json;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.EntityFrameworkCore;
using Microsoft.IdentityModel.Tokens;
using POC.AURA.Api.Auth;
using POC.AURA.Api.Data;
using POC.AURA.Api.Hubs;
using POC.AURA.Api.Repositories;
using POC.AURA.Api.Services;

// Prevent ASP.NET Core from remapping standard JWT claim names (sub, name, ...)
// to legacy .NET ClaimTypes (NameIdentifier, Name, ...).
// Without this, Context.User.FindFirst("sub") returns null in hubs/controllers
// because the bearer middleware silently maps "sub" → ClaimTypes.NameIdentifier.
JwtSecurityTokenHandler.DefaultMapInboundClaims = false;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddControllers();
builder.Services.AddHttpContextAccessor();
builder.Services.AddEndpointsApiExplorer();
builder.Services.AddSwaggerGen(c =>
{
    c.SwaggerDoc("v1", new() { Title = "POC AURA — SignalR API", Version = "v1" });
    c.AddSecurityDefinition("Bearer", new Microsoft.OpenApi.Models.OpenApiSecurityScheme
    {
        Name         = "Authorization",
        Type         = Microsoft.OpenApi.Models.SecuritySchemeType.Http,
        Scheme       = "bearer",
        BearerFormat = "JWT"
    });
});

builder.Services.AddDbContext<AppDbContext>(options =>
    options.UseSqlServer(builder.Configuration.GetConnectionString("DefaultConnection")));

// ── Application services ────────────────────────────────────────────────────
// Scoped: one instance per hub/controller invocation
builder.Services.AddScoped<IJobRepository, JobRepository>();

// Singletons: live for the entire application lifetime
builder.Services.AddSingleton<JwtService>();
builder.Services.AddSingleton<IConnectionTracker, ConnectionTracker>();
builder.Services.AddSingleton<ITransactionQueueService, TransactionQueueService>();
builder.Services.AddSingleton<IDocumentLockService, DocumentLockService>();
builder.Services.AddHostedService(p => (DocumentLockService)p.GetRequiredService<IDocumentLockService>());

// ── Authentication (JWT Bearer) ─────────────────────────────────────────────
builder.Services.AddAuthentication(JwtBearerDefaults.AuthenticationScheme)
    .AddJwtBearer(options =>
    {
        options.TokenValidationParameters = new TokenValidationParameters
        {
            ValidateIssuerSigningKey = true,
            IssuerSigningKey         = JwtService.SigningKey,
            ValidateIssuer           = false,
            ValidateAudience         = false,
            ClockSkew                = TimeSpan.Zero
        };
        // SignalR sends the token via query string because WebSocket connections
        // cannot carry custom headers.
        options.Events = new JwtBearerEvents
        {
            OnMessageReceived = context =>
            {
                var token = context.Request.Query["access_token"];
                if (!string.IsNullOrEmpty(token) &&
                    context.HttpContext.Request.Path.StartsWithSegments("/hubs"))
                    context.Token = token;
                return Task.CompletedTask;
            }
        };
    });

builder.Services.AddAuthorization();

// ── SignalR ─────────────────────────────────────────────────────────────────
builder.Services.AddSignalR(options =>
{
    options.EnableDetailedErrors  = builder.Environment.IsDevelopment();
    // Set generous timeouts — the Blazor SmartHub client sets matching values.
    // Default 30 s server timeout caused spurious disconnects under low traffic.
    options.KeepAliveInterval     = TimeSpan.FromDays(365);
    options.ClientTimeoutInterval = TimeSpan.FromDays(365);
})
// Serialise hub payloads as camelCase to match TypeScript interfaces.
.AddJsonProtocol(options =>
{
    options.PayloadSerializerOptions.PropertyNamingPolicy      = JsonNamingPolicy.CamelCase;
    options.PayloadSerializerOptions.PropertyNameCaseInsensitive = true;
});

// ── CORS ────────────────────────────────────────────────────────────────────
builder.Services.AddCors(options =>
    options.AddPolicy("AllowAll", policy =>
        policy.SetIsOriginAllowed(_ => true)
              .AllowAnyHeader()
              .AllowAnyMethod()
              .AllowCredentials()));

var app = builder.Build();

// ── Database migration (with retry for Docker startup ordering) ─────────────
using (var scope = app.Services.CreateScope())
{
    var db      = scope.ServiceProvider.GetRequiredService<AppDbContext>();
    var logger  = scope.ServiceProvider.GetRequiredService<ILogger<AppDbContext>>();
    var retries = 5;

    while (retries-- > 0)
    {
        try
        {
            db.Database.Migrate();
            logger.LogInformation("Database migration completed successfully.");
            break;
        }
        catch (Exception ex) when (retries > 0)
        {
            logger.LogWarning("Migration failed ({Left} retries left): {Msg}", retries, ex.Message);
            await Task.Delay(3_000);
        }
        catch (Exception ex)
        {
            if (app.Environment.IsDevelopment())
            {
                logger.LogWarning("Migration failed — resetting DB in dev: {Msg}", ex.Message);
                db.Database.EnsureDeleted();
                db.Database.Migrate();
            }
            else
            {
                logger.LogError(ex, "Database migration failed.");
                throw;
            }
        }
    }
}

// ── Middleware pipeline ──────────────────────────────────────────────────────
if (app.Environment.IsDevelopment())
{
    app.UseSwagger();
    app.UseSwaggerUI();
}

app.UseCors("AllowAll");
app.UseAuthentication();
app.UseAuthorization();

app.MapControllers();
app.MapHub<AuraHub>("/hubs/aura");
app.MapGet("/health", () => Results.Ok(new { status = "healthy", timestamp = DateTime.UtcNow }));

app.Run();
