namespace POC.AURA.SmartHub.Common.Models;

public class EclipseResult<T>
{
    public T? Value { get; init; }
    public string? ErrorMessage { get; init; }
    public bool IsError => ErrorMessage is not null;

    public static EclipseResult<T> Ok(T value) => new() { Value = value };
    public static EclipseResult<T> Fail(string message) => new() { ErrorMessage = message };
}
