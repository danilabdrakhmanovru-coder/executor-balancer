using System.Text.Json.Serialization;
using ExecutorBalancer.Api.Endpoints;
using ExecutorBalancer.Api.Security;
using ExecutorBalancer.Application;
using ExecutorBalancer.Infrastructure;
using Microsoft.AspNetCore.Http.Json;

var builder = WebApplication.CreateBuilder(args);

builder.WebHost.ConfigureKestrel(kestrel =>
{
    kestrel.AddServerHeader = false;
    kestrel.Limits.MaxRequestBodySize = 256 * 1024;
    kestrel.Limits.MaxRequestHeadersTotalSize = 32 * 1024;
});

builder.Services.AddOptions<IntegrationOptions>()
    .Bind(builder.Configuration.GetSection(IntegrationOptions.Section))
    .Validate(o => o.ApiKey.Length >= 24 && o.ApiKey.Distinct().Count() >= 8,
        "Integration:ApiKey должен быть случайной строкой не короче 24 символов")
    .ValidateOnStart();

builder.Services.Configure<JsonOptions>(options =>
{
    options.SerializerOptions.Converters.Add(new JsonStringEnumConverter());
    options.SerializerOptions.MaxDepth = 16;
});

builder.Services.AddProblemDetails();
builder.Services.AddOpenApi(options => options.AddDocumentTransformer<ApiKeyOpenApiTransformer>());
builder.Services.AddBalancerRateLimits();
builder.Services.AddScoped<ApiKeyFilter>();
builder.Services.AddAdminAuth(builder.Configuration);
builder.Services.AddApplication(builder.Configuration);
builder.Services.AddInfrastructure(builder.Configuration);

var app = builder.Build();

// ошибки отдаются как ProblemDetails без стека
app.UseExceptionHandler();
app.UseStatusCodePages();
app.UseMiddleware<SecurityHeadersMiddleware>();
app.UseDefaultFiles();
app.UseStaticFiles();
app.UseRateLimiter();
app.UseAuthentication();
app.UseAuthorization();

if (app.Configuration.GetValue<bool>("Swagger:Enabled"))
{
    app.MapOpenApi();
    app.UseSwaggerUI(options => options.SwaggerEndpoint("/openapi/v1.json", "Executor Balancer"));
}

app.MapGet("/health", () => Results.Ok(new { status = "ok" })).ExcludeFromDescription();
app.MapIntegrationEndpoints();
app.MapAuthEndpoints();
app.MapDashboardEndpoints();
app.MapAdminEndpoints();

app.Run();

public partial class Program
{
}
