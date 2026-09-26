namespace ExecutorBalancer.Api.Security;

/// <summary>
/// Вторая линия защиты от CSRF после SameSite=Strict: изменяющие запросы администратора должны нести
/// собственный заголовок. Чужая страница не может добавить его без CORS-разрешения, а CORS не включён.
/// </summary>
public sealed class CsrfHeaderFilter : IEndpointFilter
{
    public const string Header = "X-Requested-With";
    public const string Value = "executor-balancer";

    public ValueTask<object?> InvokeAsync(EndpointFilterInvocationContext context, EndpointFilterDelegate next)
    {
        var request = context.HttpContext.Request;
        if (!HttpMethods.IsGet(request.Method) && !HttpMethods.IsHead(request.Method)
                                               && request.Headers[Header] != Value)
        {
            return ValueTask.FromResult<object?>(Results.Problem(
                statusCode: StatusCodes.Status403Forbidden, title: "Запрос отклонён: нет заголовка защиты от подделки"));
        }

        return next(context);
    }
}
