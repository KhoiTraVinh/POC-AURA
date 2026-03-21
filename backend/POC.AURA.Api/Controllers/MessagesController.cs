using Microsoft.AspNetCore.Mvc;
using POC.AURA.Api.DTOs;
using POC.AURA.Api.Services;

namespace POC.AURA.Api.Controllers;

[ApiController]
[Route("api/[controller]")]
public class MessagesController(IMessageService messageService) : ControllerBase
{
    // Retrieve pointer (LastReadMessageId)
    [HttpGet("receipt")]
    public async Task<IActionResult> GetReceipt([FromQuery] int groupId, [FromQuery] int staffId)
    {
        var receipt = await messageService.GetReceiptAsync(groupId, staffId);
        return Ok(receipt);
    }

    [HttpGet("{groupId}")]
    public async Task<IActionResult> GetMessages(int groupId, [FromQuery] int? afterMessageId)
    {
        var messages = await messageService.GetMessagesAsync(groupId, afterMessageId);
        return Ok(messages);
    }

    [HttpPost]
    public async Task<IActionResult> SendMessage([FromBody] SendMessageRequest request)
    {
        try
        {
            var message = await messageService.SendMessageAsync(request);
            return Ok(message);
        }
        catch (ArgumentException ex)
        {
            return BadRequest(ex.Message);
        }
    }

    [HttpPost("read")]
    public async Task<IActionResult> MarkAsRead([FromBody] MarkReadRequest request)
    {
        await messageService.UpdateReadReceiptAsync(request.GroupId, request.StaffId, request.LastReadMessageId);
        return Ok();
    }
}
