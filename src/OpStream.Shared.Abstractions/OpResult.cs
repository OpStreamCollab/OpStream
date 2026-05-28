namespace OpStream.Shared.Abstractions;

/// <summary>
/// A simple result pattern to avoid exceptions for expected flow control.
/// </summary>
public record OpResult<T>(bool Success, T? Value = default, string? ErrorMessage = null)
{
    public static OpResult<T> Ok(T value) => new(true, Value: value);
    public static OpResult<T> Fail(string error) => new(false, ErrorMessage: error);
}

public record OpResult(bool Success, string? ErrorMessage = null)
{
    public static OpResult Ok() => new(true);
    public static OpResult Fail(string error) => new(false, ErrorMessage: error);
}
