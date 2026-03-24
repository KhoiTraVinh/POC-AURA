using System.IdentityModel.Tokens.Jwt;
using System.Text.Json;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.EntityFrameworkCore;
using Microsoft.IdentityModel.Tokens;
using POC.AURA.Api.Auth;
using POC.AURA.Api.Data;
using POC.AURA.Api.Entities;
using POC.AURA.Api.Hubs;
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
    c.SwaggerDoc("v1", new() { Title = "POC AURA - SignalR API", Version = "v1" });
    c.AddSecurityDefinition("Bearer", new Microsoft.OpenApi.Models.OpenApiSecurityScheme
    {
        Name = "Authorization",
        Type = Microsoft.OpenApi.Models.SecuritySchemeType.Http,
        Scheme = "bearer",
        BearerFormat = "JWT"
    });
});

builder.Services.AddDbContext<AppDbContext>(options =>
    options.UseSqlServer(builder.Configuration.GetConnectionString("DefaultConnection")));

// Register services
builder.Services.AddScoped<IMessageService, MessageService>();
builder.Services.AddSingleton<JwtService>();

// Transaction: singleton only — no background worker needed (fail-fast, no queue)
builder.Services.AddSingleton<ITransactionQueueService, TransactionQueueService>();

// Document Lock: singleton + background service (same instance)
builder.Services.AddSingleton<DocumentLockService>();
builder.Services.AddSingleton<IDocumentLockService>(sp => sp.GetRequiredService<DocumentLockService>());
builder.Services.AddHostedService(sp => sp.GetRequiredService<DocumentLockService>());

// JWT Authentication for PrintHub
builder.Services.AddAuthentication(JwtBearerDefaults.AuthenticationScheme)
    .AddJwtBearer(options =>
    {
        options.TokenValidationParameters = new TokenValidationParameters
        {
            ValidateIssuerSigningKey = true,
            IssuerSigningKey = JwtService.SigningKey,
            ValidateIssuer = false,
            ValidateAudience = false,
            ClockSkew = TimeSpan.Zero // Strict 10-min expiry, no grace period
        };
        // SignalR sends token via query string (WebSocket doesn't support headers)
        options.Events = new JwtBearerEvents
        {
            OnMessageReceived = context =>
            {
                var accessToken = context.Request.Query["access_token"];
                if (!string.IsNullOrEmpty(accessToken) &&
                    context.HttpContext.Request.Path.StartsWithSegments("/hubs"))
                {
                    context.Token = accessToken;
                }
                return Task.CompletedTask;
            }
        };
    });

builder.Services.AddAuthorization();

builder.Services.AddSignalR(options =>
{
    options.EnableDetailedErrors = builder.Environment.IsDevelopment();
    options.KeepAliveInterval = TimeSpan.FromDays(365);
    options.ClientTimeoutInterval = TimeSpan.FromDays(365);
})
// C# records/anonymous types default to PascalCase in System.Text.Json.
// Force camelCase so all hubs match the TypeScript interfaces on the frontend.
// Affects ALL hubs: ChatHub, PrintHub, TransactionHub, DocumentHub.
.AddJsonProtocol(options =>
{
    options.PayloadSerializerOptions.PropertyNamingPolicy = JsonNamingPolicy.CamelCase;
});

builder.Services.AddCors(options =>
{
    options.AddPolicy("AllowAngular", policy =>
    {
        policy
            .SetIsOriginAllowed(_ => true)
            .AllowAnyHeader()
            .AllowAnyMethod()
            .AllowCredentials();
    });
});

var app = builder.Build();

// Auto-migrate on startup
using (var scope = app.Services.CreateScope())
{
    var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
    var logger = scope.ServiceProvider.GetRequiredService<ILogger<AppDbContext>>();

    var retries = 5;
    while (retries-- > 0)
    {
        try
        {
            db.Database.Migrate();
            logger.LogInformation("Database migration completed.");

            if (!db.Groups.Any())
            {
                db.Groups.AddRange(
                    new Group { GroupName = "Demo Group 1", CreatedAt = DateTime.UtcNow },
                    new Group { GroupName = "Demo Group 2", CreatedAt = DateTime.UtcNow }
                );
                db.SaveChanges();
                logger.LogInformation("Seed data created: 2 demo groups.");
            }
            break;
        }
        catch (Exception ex) when (retries > 0)
        {
            logger.LogWarning("Migration failed ({Retries} retries left): {Message}", retries, ex.Message);
            await Task.Delay(3000);
        }
        catch (Exception ex)
        {
            if (app.Environment.IsDevelopment())
            {
                logger.LogWarning("Migration failed in dev mode, resetting database: {Message}", ex.Message);
                db.Database.EnsureDeleted();
                db.Database.Migrate();
                logger.LogInformation("Database reset and migrated successfully.");
            }
            else
            {
                logger.LogError(ex, "Migration failed.");
                throw;
            }
        }
    }
}

if (app.Environment.IsDevelopment())
{
    app.UseSwagger();
    app.UseSwaggerUI();
}

app.UseCors("AllowAngular");
app.UseAuthentication();
app.UseAuthorization();

app.Use((context, next) =>
{
    Console.WriteLine($"Request: {context.Request.Method} {context.Request.Path}");
    return next();
});

app.MapControllers();
app.MapHub<ChatHub>("/hubs/chat");
app.MapHub<PrintHub>("/hubs/print");
app.MapHub<TransactionHub>("/hubs/transaction");
app.MapHub<DocumentHub>("/hubs/document");

app.MapGet("/health", () => Results.Ok(new { status = "healthy", timestamp = DateTime.UtcNow }));

app.Run();
