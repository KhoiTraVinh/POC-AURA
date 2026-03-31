using POC.AURA.SmartHub.Common;
using POC.AURA.SmartHub.Data.Entities;

namespace POC.AURA.SmartHub.Data;

public interface IServerConnectionRepository
{
    Task<List<ServerConnection>> GetAllAsync();
    Task<ServerConnection?> GetByIdAsync(int id);
    Task<ServerConnection> AddAsync(ServerConnection connection);
    Task UpdateStatusAsync(int id, ConnectionStatus status, string? message = null);
    Task DeleteAsync(int id);
    Task SaveTokenAsync(int connectionId, string accessToken, DateTime expiredAt);
    Task<AuthToken?> GetTokenAsync(int connectionId);
    Task DeleteTokenAsync(int connectionId);
}
