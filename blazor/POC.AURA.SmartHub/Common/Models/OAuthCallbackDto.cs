using System.ComponentModel.DataAnnotations;

namespace POC.AURA.SmartHub.Common.Models;

public class OAuthCallbackDto
{
    [Required] public string Code         { get; set; } = "";
    [Required] public string Scope        { get; set; } = "";
               public string State        { get; set; } = "";
               public string SessionState { get; set; } = "";
}
