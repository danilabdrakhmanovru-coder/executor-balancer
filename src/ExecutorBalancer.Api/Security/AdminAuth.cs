using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using ExecutorBalancer.Application;
using ExecutorBalancer.Domain;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.Extensions.Options;

namespace ExecutorBalancer.Api.Security;

public sealed class AdminOptions
{
    public const string Section = "Admin";

    public string Password { get; set; } = "";
}

public sealed record LoginRequest(string? Password);

/// <summary>Вход в дашборд по паролю администратора. Сессия — HttpOnly cookie с SameSite=Strict.</summary>
public static class AdminAuth
{
    public const string LoginPolicy = "login";

    public static IServiceCollection AddAdminAuth(this IServiceCollection services, IConfiguration configuration)
    {
        services.AddOptions<AdminOptions>()
            .Bind(configuration.GetSection(AdminOptions.Section))
            .Validate(o => o.Password.Length >= 12, "Admin:Password должен быть не короче 12 символов")
            .ValidateOnStart();

        services.AddAuthentication(CookieAuthenticationDefaults.AuthenticationScheme)
            .AddCookie(options =>
            {
                options.Cookie.Name = "eb_admin";
                options.Cookie.HttpOnly = true;
                options.Cookie.SameSite = SameSiteMode.Strict;
                options.Cookie.SecurePolicy = CookieSecurePolicy.SameAsRequest;
                options.ExpireTimeSpan = TimeSpan.FromHours(8);
                options.SlidingExpiration = true;
                // API отвечает кодами, а не редиректами на страницу входа
                options.Events.OnRedirectToLogin = context =>
                {
                    context.Response.StatusCode = StatusCodes.Status401Unauthorized;
                    return Task.CompletedTask;
                };
                options.Events.OnRedirectToAccessDenied = context =>
                {
                    context.Response.StatusCode = StatusCodes.Status403Forbidden;
                    return Task.CompletedTask;
                };
            });
        services.AddAuthorization();
        return services;
    }

    public static IEndpointRouteBuilder MapAuthEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/auth").WithTags("Вход");
        group.MapPost("/login", Login).RequireRateLimiting(LoginPolicy);
        // приведение к Delegate: иначе метод с одним HttpContext считается RequestDelegate и результат теряется
        group.MapPost("/logout", (Delegate)Logout);
        group.MapGet("/me", (HttpContext context) => context.User.Identity?.IsAuthenticated == true
            ? Results.Ok(new { name = context.User.Identity.Name })
            : Results.Unauthorized());
        return app;
    }

    private static async Task<IResult> Login(LoginRequest request, HttpContext context, IOptions<AdminOptions> options,
        IBalancerDbContext db, CancellationToken ct)
    {
        // сравниваем хеши одинаковой длины: время проверки не выдаёт даже длину пароля
        var expected = SHA256.HashData(Encoding.UTF8.GetBytes(options.Value.Password));
        var provided = SHA256.HashData(Encoding.UTF8.GetBytes(request.Password ?? ""));
        var ok = CryptographicOperations.FixedTimeEquals(provided, expected);

        db.AuditEntries.Add(new AuditEntry
        {
            Actor = "admin",
            Action = ok ? "login" : "login_failed",
            Entity = "session",
            EntityId = context.Connection.RemoteIpAddress?.ToString() ?? "",
            CreatedAt = DateTimeOffset.UtcNow,
        });
        await db.SaveChangesAsync(ct);

        if (!ok)
        {
            return Results.Problem(statusCode: StatusCodes.Status401Unauthorized, title: "Неверный пароль");
        }

        var identity = new ClaimsIdentity(new[] { new Claim(ClaimTypes.Name, "admin") },
            CookieAuthenticationDefaults.AuthenticationScheme);
        await context.SignInAsync(CookieAuthenticationDefaults.AuthenticationScheme, new ClaimsPrincipal(identity));
        return Results.NoContent();
    }

    private static async Task<IResult> Logout(HttpContext context)
    {
        await context.SignOutAsync(CookieAuthenticationDefaults.AuthenticationScheme);
        return Results.NoContent();
    }
}
