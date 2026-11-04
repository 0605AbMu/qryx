using Microsoft.AspNetCore.Mvc.ModelBinding;
using Microsoft.Extensions.DependencyInjection;
using Qryx.AspNetCore.Internal;
using Qryx.Expressions.Options;

namespace Qryx.AspNetCore.ModelBinding;

/// <summary>
/// Model binder for Qryx request DTOs in MVC controllers.
/// Binds <c>page</c>, <c>pageSize</c>, <c>sortBy</c> (repeated values are joined with ','), <c>isDescending</c>,
/// <c>afterCursor</c>, <c>includeTotal</c> and every [Filter] property using cached,
/// compiled, invariant-culture parsers (any <see cref="IParsable{TSelf}"/> such as <c>Range&lt;T&gt;</c>,
/// <see cref="DateOnly"/>, <see cref="TimeOnly"/>, <see cref="DateTimeOffset"/>, <see cref="Guid"/>; enums by
/// case-insensitive name or defined numeric value; nullable wrappers; arrays, lists and sets for multi-value keys).
/// </summary>
/// <remarks>
/// Page and PageSize are normalized with the <see cref="QryxOptions"/> from DI (fallback <see cref="QryxOptions.Global"/>).
/// Unparsable values are added to <c>ModelState</c> under the camelCase query key, so <c>[ApiController]</c>
/// endpoints answer HTTP 400; without <c>[ApiController]</c> check <c>ModelState.IsValid</c> yourself.
/// </remarks>
public sealed class QryxDtoModelBinder(Type dtoType) : IModelBinder
{
    public Task BindModelAsync(ModelBindingContext bindingContext)
    {
        ArgumentNullException.ThrowIfNull(bindingContext);

        var plan = QryxBindingPlan.For(dtoType);
        var dto = plan.Metadata.Factory();
        if (dto == null)
        {
            bindingContext.Result = ModelBindingResult.Failed();
            return Task.CompletedTask;
        }

        var options = bindingContext.HttpContext.RequestServices.GetService<QryxOptions>() ?? QryxOptions.Global;
        var errors = plan.Bind(dto, new ValueProviderSource(bindingContext.ValueProvider), options);

        if (errors != null)
        {
            foreach (var error in errors)
            {
                bindingContext.ModelState.TryAddModelError(error.Key, error.Message);
            }
        }

        bindingContext.Result = ModelBindingResult.Success(dto);
        return Task.CompletedTask;
    }
}
