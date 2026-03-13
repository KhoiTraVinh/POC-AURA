using Microsoft.EntityFrameworkCore;
using POC.AURA.Api.Data;
using POC.AURA.Api.Entities;
using POC.AURA.Api.Hubs;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddControllers();
builder.Services.AddEndpointsApiExplorer();
builder.Services.AddSwaggerGen(c =>
{
    c.SwaggerDoc("v1", new() { Title = "POC AURA - SignalR API", Version = "v1" });
});

builder.Services.AddDbContext<AppDbContext>(options =>
    options.UseSqlServer(builder.Configuration.GetConnectionString("DefaultConnection")));

builder.Services.AddSignalR(options =>
{
    options.EnableDetailedErrors = builder.Environment.IsDevelopment();
});

builder.Services.AddCors(options =>
{
    options.AddPolicy("AllowAngular", policy =>
    {
        policy
            .SetIsOriginAllowed(origin => true) // Allow any origin for dev
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

    // Retry vì SQL Server container có thể chưa sẵn sàng ngay
    var retries = 5;
    while (retries-- > 0)
    {
        try
        {
            db.Database.Migrate();
            logger.LogInformation("Database migration completed.");

            // Seed Group mẫu để demo (idempotent)
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
                // Dev only: DB state bị lệch → xóa và tạo lại
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

app.Use((context, next) =>
{
    Console.WriteLine($"Request: {context.Request.Method} {context.Request.Path}");
    return next();
});

app.MapControllers();
app.MapHub<ChatHub>("/hubs/chat");

app.MapGet("/health", () => Results.Ok(new { status = "healthy", timestamp = DateTime.UtcNow }));

app.Run();
