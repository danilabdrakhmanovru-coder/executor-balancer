using System.Text.Json.Serialization;
using ExecutorBalancer.Api.Endpoints;
using ExecutorBalancer.Api.Security;
using ExecutorBalancer.Application;
using ExecutorBalancer.Infrastructure;
using Microsoft.AspNetCore.Http.Json;
using Microsoft.AspNetCore.HttpOverrides;

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

builder.Services.AddOptions<DemoOptions>().Bind(builder.Configuration.GetSection(DemoOptions.Section));
builder.Services.AddProblemDetails();
builder.Services.AddOpenApi(options => options.AddDocumentTransformer<ApiKeyOpenApiTransformer>());
builder.Services.AddBalancerRateLimits();
builder.Services.AddScoped<ApiKeyFilter>();
builder.Services.AddAdminAuth(builder.Configuration);
builder.Services.AddApplication(builder.Configuration);
builder.Services.AddInfrastructure(builder.Configuration);
// после миграций и восстановления Redis (StartupInitializer): в демо заполняет пустые отделы, в бою сразу завершается
builder.Services.AddHostedService<DemoWarmup>();
// гостевой вход в демо: полчаса без изменений от гостей — демо возвращается в исходное
builder.Services.AddHostedService<DemoAutoReset>();

var app = builder.Build();

// за обратным прокси (Caddy на сервере): адрес посетителя и https — из его заголовков, иначе лимиты входа
// считались бы общими на всех, а cookie не получили бы признак Secure. Сам сервис наружу не открыт
// (порт только на 127.0.0.1), поэтому заголовкам доверяем от последнего прокси
if (app.Configuration.GetValue<bool>("Proxy:Enabled"))
{
    var forwarded = new ForwardedHeadersOptions
    {
        ForwardedHeaders = ForwardedHeaders.XForwardedFor | ForwardedHeaders.XForwardedProto,
        ForwardLimit = 1,
    };
    forwarded.KnownIPNetworks.Clear();
    forwarded.KnownProxies.Clear();
    app.UseForwardedHeaders(forwarded);
}

// ошибки отдаются как ProblemDetails без стека
app.UseExceptionHandler();
app.UseStatusCodePages();
app.UseMiddleware<SecurityHeadersMiddleware>();
app.UseDefaultFiles();
app.UseStaticFiles(new StaticFileOptions
{
    // браузер сверяется с сервером при каждой загрузке (ответ 304, если файл не менялся):
    // после обновления сервиса старый JS из кэша не смешивается с новой страницей
    OnPrepareResponse = context => context.Context.Response.Headers.CacheControl = "no-cache",
});
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
app.MapDemoEndpoints();

app.Run();

public partial class Program
{
}
