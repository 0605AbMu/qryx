namespace Qryx.Core.Exceptions;

/// <summary>
/// Thrown when a client-supplied query value is invalid (unparsable filter value, disallowed sort or cursor
/// property, oversized collection). Represents a client error: Qryx.AspNetCore maps it to HTTP 400.
/// </summary>
public sealed class QryxRequestException : ArgumentException
{
    /// <summary>Human-readable reason without the " (Parameter '...')" suffix added by <see cref="ArgumentException"/>.</summary>
    public string Reason { get; }

    public QryxRequestException(string parameterName, string reason)
        : base(reason, parameterName)
    {
        Reason = reason;
    }

    public QryxRequestException(string parameterName, string reason, Exception? innerException)
        : base(reason, parameterName, innerException)
    {
        Reason = reason;
    }
}
