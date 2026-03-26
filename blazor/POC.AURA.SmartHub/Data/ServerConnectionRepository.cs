using Microsoft.EntityFrameworkCore;
using POC.AURA.SmartHub.Common;
using POC.AURA.SmartHub.Data.Entities;

namespace POC.AURA.SmartHub.Data;

public class ServerConnectionRepository(IDbContextFactory<SmartHubDbContext> factory)
    : IServerConnectionRepository
{
    public async Task<List<ServerConnection>> GetAllAsync()
    {
        await using var db = await factory.CreateDbContextAsync();
        return await db.ServerConnections.Include(s => s.AuthToken).ToListAsync();
    }

    public async Task<ServerConnection?> GetByIdAsync(int id)
    {
        await using var db = await factory.CreateDbContextAsync();
        return await db.ServerConnections.Include(s => s.AuthToken)
            .FirstOrDefaultAsync(s => s.Id == id);
    }

    public async Task<ServerConnection> AddAsync(ServerConnection connection)
    {
        await using var db = await factory.CreateDbContextAsync();
        db.ServerConnections.Add(connection);
        await db.SaveChangesAsync();
        return connection;
    }

    public async Task UpdateStatusAsync(int id, ConnectionStatus status, string? message = null)
    {
        await using var db = await factory.CreateDbContextAsync();
        var conn = await db.ServerConnections.FindAsync(id);
        if (conn is null) return;
        conn.Status = status;
        conn.Message = message;
        conn.UpdatedAt = DateTime.UtcNow;
        await db.SaveChangesAsync();
    }

    public async Task DeleteAsync(int id)
    {
        await using var db = await factory.CreateDbContextAsync();
        var conn = await db.ServerConnections.FindAsync(id);
        if (conn is null) return;
        db.ServerConnections.Remove(conn);
        await db.SaveChangesAsync();
    }

    public async Task SaveTokenAsync(int connectionId, string accessToken, DateTime expiredAt)
    {
        await using var db = await factory.CreateDbContextAsync();
        var token = await db.AuthTokens.FindAsync(connectionId);
        if (token is null)
        {
            db.AuthTokens.Add(new AuthToken
            {
                ServerConnectionId = connectionId,
                AccessToken = accessToken,
                ExpiredAt = expiredAt,
                CreatedAt = DateTime.UtcNow,
                UpdatedAt = DateTime.UtcNow
            });
        }
        else
        {
            token.AccessToken = accessToken;
            token.ExpiredAt = expiredAt;
            token.UpdatedAt = DateTime.UtcNow;
        }
        await db.SaveChangesAsync();
    }

    public async Task<AuthToken?> GetTokenAsync(int connectionId)
    {
        await using var db = await factory.CreateDbContextAsync();
        return await db.AuthTokens.FindAsync(connectionId);
    }

    public async Task DeleteTokenAsync(int connectionId)
    {
        await using var db = await factory.CreateDbContextAsync();
        var token = await db.AuthTokens.FindAsync(connectionId);
        if (token is null) return;
        db.AuthTokens.Remove(token);
        await db.SaveChangesAsync();
    }
}
