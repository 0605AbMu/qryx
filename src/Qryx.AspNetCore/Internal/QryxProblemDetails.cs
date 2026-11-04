using System.Diagnostics.CodeAnalysis;
using System.Text.Json;
using Qryx.Core.Exceptions;

namespace Qryx.AspNetCore.Internal;

/// <summary>Shared helpers that map Qryx client errors to the validation problem shape (RFC 9457).</summary>
internal static class QryxProblemDetails
{
    private const string FallbackKey = "request";
    private const int MaxInnerExceptionDepth = 8;

    /// <summary>Error dictionary key: the camelCase query parameter name (e.g. "SortBy" -> "sortBy").</summary>
    public static string GetErrorKey(string? parameterName) =>
        string.IsNullOrWhiteSpace(parameterName)
            ? FallbackKey
            : JsonNamingPolicy.CamelCase.ConvertName(parameterName);

    /// <summary>Finds a <see cref="QryxRequestException"/> in the exception or its inner-exception chain.</summary>
    public static bool TryFind(Exception exception, [NotNullWhen(true)] out QryxRequestException? requestException)
    {
        var current = exception;
        for (int depth = 0; current != null && depth < MaxInnerExceptionDepth; depth++)
        {
            if (current is QryxRequestException found)
            {
                requestException = found;
                return true;
            }

            current = current.InnerException;
        }

        requestException = null;
        return false;
    }

    public static Dictionary<string, string[]> ToErrors(QryxRequestException exception) =>
        new(StringComparer.Ordinal) { [GetErrorKey(exception.ParamName)] = [exception.Reason] };

    public static Dictionary<string, string[]> ToErrors(List<QryxBindingError> errors)
    {
        var grouped = new Dictionary<string, List<string>>(StringComparer.Ordinal);
        foreach (var error in errors)
        {
            if (!grouped.TryGetValue(error.Key, out var messages))
            {
                messages = [];
                grouped[error.Key] = messages;
            }

            messages.Add(error.Message);
        }

        var result = new Dictionary<string, string[]>(grouped.Count, StringComparer.Ordinal);
        foreach (var (key, messages) in grouped)
        {
            result[key] = [.. messages];
        }

        return result;
    }
}
