using Microsoft.AspNetCore.OpenApi;
using Microsoft.OpenApi;

namespace ExecutorBalancer.Api.Security;

/// <summary>Описывает в OpenAPI ключ X-Api-Key для методов интеграции — в Swagger UI работает «Try it out».</summary>
public sealed class ApiKeyOpenApiTransformer : IOpenApiDocumentTransformer
{
    private const string SchemeId = "IntegrationApiKey";

    public Task TransformAsync(OpenApiDocument document, OpenApiDocumentTransformerContext context,
        CancellationToken cancellationToken)
    {
        document.Components ??= new OpenApiComponents();
        document.Components.SecuritySchemes ??= new Dictionary<string, IOpenApiSecurityScheme>();
        document.Components.SecuritySchemes[SchemeId] = new OpenApiSecurityScheme
        {
            Type = SecuritySchemeType.ApiKey,
            In = ParameterLocation.Header,
            Name = ApiKeyFilter.Header,
            Description = "Ключ интеграции АИС (Integration:ApiKey)",
        };

        foreach (var (path, item) in document.Paths)
        {
            if (!path.StartsWith("/api/integration", StringComparison.Ordinal) || item.Operations is null)
            {
                continue;
            }

            foreach (var operation in item.Operations.Values)
            {
                operation.Security ??= [];
                operation.Security.Add(new OpenApiSecurityRequirement
                {
                    [new OpenApiSecuritySchemeReference(SchemeId, document)] = [],
                });
            }
        }

        return Task.CompletedTask;
    }
}
