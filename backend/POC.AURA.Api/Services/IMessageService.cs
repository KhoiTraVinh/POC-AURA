using POC.AURA.Api.DTOs;

namespace POC.AURA.Api.Services;

public interface IMessageService
{
    Task<MessageDto> SendMessageAsync(SendMessageRequest request);
    Task<List<MessageDto>> GetMessagesAsync(int groupId, int? afterMessageId);
    Task<ReadReceiptDto> GetReceiptAsync(int groupId, int staffId);
    Task UpdateReadReceiptAsync(int groupId, int staffId, int lastReadMessageId);
}
