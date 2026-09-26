using System.Security.Cryptography;
using System.Text;
using Microsoft.Extensions.Options;

namespace ExecutorBalancer.Api.Security;

public sealed class IntegrationOptions
{
    public const string Section = "Integration";

    /// <summary>Ключ, с которым АИС вызывает балансировщик.</summary>
    public string ApiKey { get; set; } = "";
}

/// <summary>Проверка ключа X-Api-Key для вызовов из АИС. Сравнение за постоянное время.</summary>
public sealed class ApiKeyFilter(IOptions<IntegrationOptions> options) : IEndpointFilter
{
    public const string Header = "X-Api-Key";

    private readonly byte[] _expected = Encoding.UTF8.GetBytes(options.Value.ApiKey);

    public ValueTask<object?> InvokeAsync(EndpointFilterInvocationContext context, EndpointFilterDelegate next)
    {
        var provided = context.HttpContext.Request.Headers[Header].ToString();
        if (provided.Length == 0 || !CryptographicOperations.FixedTimeEquals(Encoding.UTF8.GetBytes(provided), _expected))
        {
            return ValueTask.FromResult<object?>(Results.Problem(
                statusCode: StatusCodes.Status401Unauthorized, title: "Неверный ключ интеграции"));
        }

        return next(context);
    }
}
