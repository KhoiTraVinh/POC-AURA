namespace POC.AURA.SmartHub.Common.Models;

public class AuthenticationResult
{
    public bool IsAuthenticated { get; init; }
    public int? StatusCode { get; init; }
    public string? ErrorMessage { get; init; }

    public static AuthenticationResult Success() => new() { IsAuthenticated = true };
    public static AuthenticationResult Failure(int? statusCode, string errorMessage) =>
        new() { IsAuthenticated = false, StatusCode = statusCode, ErrorMessage = errorMessage };
}
