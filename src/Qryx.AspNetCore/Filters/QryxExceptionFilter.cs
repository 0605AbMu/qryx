using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Filters;
using Microsoft.AspNetCore.Mvc.Infrastructure;
using Microsoft.AspNetCore.Mvc.ModelBinding;
using Microsoft.Extensions.DependencyInjection;
using Qryx.AspNetCore.Internal;
using Qryx.Core.Exceptions;

namespace Qryx.AspNetCore.Filters;

/// <summary>
/// Global MVC exception filter (registered by <c>services.AddQryx()</c>) that turns a
/// <see cref="QryxRequestException"/> thrown by an action into HTTP 400 <see cref="ValidationProblemDetails"/>
/// (<c>errors: { "&lt;camelCaseParam&gt;": ["&lt;reason&gt;"] }</c>), matching the <c>[ApiController]</c>
/// model-validation response shape. Other exceptions are left untouched.
/// </summary>
/// <remarks>
/// MVC exception filters only observe exceptions thrown while the action runs. An <see cref="IAsyncEnumerable{T}"/>
/// that builds the Qryx query lazily (inside an <c>async</c> iterator) fails during result execution instead;
/// build the query eagerly (e.g. <c>ToPagedAsyncEnumerable</c>) so invalid input is still reported as 400.
/// </remarks>
public sealed class QryxExceptionFilter : IExceptionFilter
{
    private static readonly string[] ProblemContentTypes = ["application/problem+json", "application/problem+xml"];

    public void OnException(ExceptionContext context)
    {
        ArgumentNullException.ThrowIfNull(context);

        if (context.ExceptionHandled || !QryxProblemDetails.TryFind(context.Exception, out var requestException))
            return;

        var modelState = new ModelStateDictionary();
        modelState.AddModelError(QryxProblemDetails.GetErrorKey(requestException.ParamName), requestException.Reason);

        var httpContext = context.HttpContext;
        var factory = httpContext.RequestServices.GetService<ProblemDetailsFactory>();
        var problem = factory?.CreateValidationProblemDetails(httpContext, modelState, StatusCodes.Status400BadRequest)
                      ?? new ValidationProblemDetails(modelState) { Status = StatusCodes.Status400BadRequest };

        var result = new BadRequestObjectResult(problem);
        foreach (var contentType in ProblemContentTypes)
        {
            result.ContentTypes.Add(contentType);
        }

        context.Result = result;
        context.ExceptionHandled = true;
    }
}
