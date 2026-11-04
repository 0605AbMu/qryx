using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Qryx.AspNetCore.Internal;
using Qryx.Core.Contracts;
using Qryx.Core.Exceptions;
using Qryx.Expressions.Options;

namespace Qryx.AspNetCore.Filters;

/// <summary>
/// Endpoint filter for Minimal APIs (attached with <c>.WithQryx()</c>) that:
/// <list type="bullet">
/// <item>enforces the pagination DoS guards on every <see cref="IQryxRequest"/> argument using the
/// <see cref="QryxOptions"/> registered in DI (falling back to <see cref="QryxOptions.Global"/>):
/// Page &lt; 1 becomes 1, a missing or non-positive PageSize becomes <see cref="QryxOptions.DefaultPageSize"/>,
/// and PageSize is capped at <see cref="QryxOptions.MaxPageSize"/>;</item>
/// <item>turns a <see cref="QryxRequestException"/> thrown by the handler into an HTTP 400
/// validation problem (<c>errors: { "&lt;camelCaseParam&gt;": ["&lt;reason&gt;"] }</c>);</item>
/// <item>reports Minimal API binding failures of Qryx DTO properties (e.g. an unknown enum name) as the same
/// validation problem instead of an empty 400 (only when <c>RouteHandlerOptions.ThrowOnBadRequest</c> is false,
/// which is the default outside Development);</item>
/// <item>starts <see cref="IAsyncEnumerable{T}"/> results before the response is written, so query errors raised
/// inside lazy iterators also become 400 while the items are still streamed.</item>
/// </list>
/// </summary>
public sealed class QryxEndpointFilter : IEndpointFilter
{
    public async ValueTask<object?> InvokeAsync(EndpointFilterInvocationContext context, EndpointFilterDelegate next)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(next);

        var httpContext = context.HttpContext;
        var options = httpContext.RequestServices.GetService<QryxOptions>() ?? QryxOptions.Global;

        var hasQryxArgument = false;
        var arguments = context.Arguments;
        for (int i = 0; i < arguments.Count; i++)
        {
            if (arguments[i] is IQryxRequest request)
            {
                hasQryxArgument = true;
                QryxBindingPlan.For(request.GetType()).ApplyGuards(request, request, options);
            }
        }

        if (!hasQryxArgument)
            return await next(context).ConfigureAwait(false);

        // Minimal APIs still run filters after a parameter binding failure (status already set to 400,
        // the handler itself is skipped). Re-validate the Qryx DTOs to tell the client which key was invalid.
        if (httpContext.Response.StatusCode == StatusCodes.Status400BadRequest && !httpContext.Response.HasStarted)
        {
            var bindingErrors = CollectBindingErrors(arguments, httpContext.Request.Query, options)
                                ?? CollectEnumErrors(arguments, httpContext.Request.Query, checkCase: true);
            if (bindingErrors != null)
                return Results.ValidationProblem(QryxProblemDetails.ToErrors(bindingErrors));
        }
        else
        {
            // Enum properties are bound by Minimal APIs (Enum.TryParse): "?status=Active,Blocked" silently becomes
            // the flags value Active|Blocked. Reject it instead of returning wrong rows.
            var enumErrors = CollectEnumErrors(arguments, httpContext.Request.Query, checkCase: false);
            if (enumErrors != null)
                return Results.ValidationProblem(QryxProblemDetails.ToErrors(enumErrors));
        }

        try
        {
            var result = await next(context).ConfigureAwait(false);

            if (result != null && AsyncEnumerablePrefetcher.TryGetPrefetcher(result.GetType(), out var prefetch))
            {
                result = await prefetch(result, httpContext.RequestAborted).ConfigureAwait(false);
            }

            return result;
        }
        catch (Exception ex) when (QryxProblemDetails.TryFind(ex, out var requestException))
        {
            return Results.ValidationProblem(QryxProblemDetails.ToErrors(requestException));
        }
    }

    private static List<QryxBindingError>? CollectEnumErrors(IList<object?> arguments, IQueryCollection query, bool checkCase)
    {
        List<QryxBindingError>? errors = null;
        for (int i = 0; i < arguments.Count; i++)
        {
            if (arguments[i] is IQryxRequest request)
            {
                var found = QryxBindingPlan.For(request.GetType()).ValidateMinimalApiEnums(query, checkCase);
                if (found != null)
                    (errors ??= []).AddRange(found);
            }
        }

        return errors;
    }

    private static List<QryxBindingError>? CollectBindingErrors(IList<object?> arguments, IQueryCollection query, QryxOptions options)
    {
        List<QryxBindingError>? errors = null;
        var source = new QueryCollectionSource(query);

        for (int i = 0; i < arguments.Count; i++)
        {
            if (arguments[i] is IQryxRequest request)
            {
                var found = QryxBindingPlan.For(request.GetType()).Bind(target: null, source, options);
                if (found != null)
                    (errors ??= []).AddRange(found);
            }
        }

        return errors;
    }
}
