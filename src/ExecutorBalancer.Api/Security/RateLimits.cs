using System.Threading.RateLimiting;
using Microsoft.AspNetCore.RateLimiting;

namespace ExecutorBalancer.Api.Security;

public static class RateLimits
{
    public const string Integration = "integration";

    public static IServiceCollection AddBalancerRateLimits(this IServiceCollection services)
    {
        services.AddRateLimiter(options =>
        {
            options.RejectionStatusCode = StatusCodes.Status429TooManyRequests;
            // пики нагрузки от АИС допустимы, но не бесконечный поток с одного адреса
            options.AddPolicy(Integration, context => RateLimitPartition.GetTokenBucketLimiter(
                context.Connection.RemoteIpAddress?.ToString() ?? "unknown",
                _ => new TokenBucketRateLimiterOptions
                {
                    TokenLimit = 2000,
                    TokensPerPeriod = 500,
                    ReplenishmentPeriod = TimeSpan.FromSeconds(1),
                    QueueLimit = 0,
                    AutoReplenishment = true,
                }));
            // подбор пароля: 5 попыток в минуту с адреса
            options.AddPolicy(AdminAuth.LoginPolicy, context => RateLimitPartition.GetFixedWindowLimiter(
                context.Connection.RemoteIpAddress?.ToString() ?? "unknown",
                _ => new FixedWindowRateLimiterOptions
                {
                    PermitLimit = 5,
                    Window = TimeSpan.FromMinutes(1),
                    QueueLimit = 0,
                }));
            options.GlobalLimiter = PartitionedRateLimiter.Create<HttpContext, string>(context =>
                RateLimitPartition.GetFixedWindowLimiter(
                    context.Connection.RemoteIpAddress?.ToString() ?? "unknown",
                    _ => new FixedWindowRateLimiterOptions
                    {
                        PermitLimit = 3000,
                        Window = TimeSpan.FromSeconds(10),
                        QueueLimit = 0,
                    }));
        });
        return services;
    }
}
