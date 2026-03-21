namespace POC.AURA.Api.Infrastructure;

public interface IConnectionManager
{
    void AddConnection(string connectionId, string groupId);
    void RemoveConnection(string connectionId);
    string? GetGroupId(string connectionId);
}
