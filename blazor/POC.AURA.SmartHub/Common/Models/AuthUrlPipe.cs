namespace POC.AURA.SmartHub.Common.Models;

/// <summary>Returned by SignInAsync — contains the OAuth authorization URL and the PKCE state.</summary>
public record AuthUrlPipe(string AuthUrl, string State);
