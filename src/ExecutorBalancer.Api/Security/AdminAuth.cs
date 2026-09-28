using System.Globalization;
using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using ExecutorBalancer.Application;
using ExecutorBalancer.Application.Configuration;
using ExecutorBalancer.Application.Users;
using ExecutorBalancer.Domain;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Options;

namespace ExecutorBalancer.Api.Security;

public sealed class AdminOptions
{
    public const string Section = "Admin";

    public string Password { get; set; } = "";

    /// <summary>
    /// Гостевой вход без пароля — для жюри и тех, кому дали только ссылку: пусто — выключен, Viewer — только
    /// просмотр, Manager — можно ещё отправлять на перерыв, менять режим «больше нормы», запускать ИИ-разбор.
    /// Администратором гость не бывает. Выключили — открытые гостевые сессии закрываются.
    /// </summary>
    public string GuestRole { get; set; } = "";

    /// <summary>Роль гостя или null, если гостевой вход выключен или задан неверно.</summary>
    public UserRole? Guest => Enum.TryParse<UserRole>(GuestRole, ignoreCase: true, out var role)
        && role is UserRole.Viewer or UserRole.Manager ? role : null;
}

public sealed record LoginRequest(string? Login, string? Password);

/// <summary>Политики доступа: по умолчанию — любой вошедший (наблюдатель), выше — руководитель и администратор.</summary>
public static class Policies
{
    public const string Manager = "manager";
    public const string Admin = "admin";

    public const string DepartmentsClaim = "eb:departments";
    public const string StampClaim = "eb:stamp";
    public const string DisplayNameClaim = "eb:name";

    /// <summary>Роль и отделы вошедшего пользователя. Отделы в сессии: «*» — все, иначе номера через запятую.</summary>
    public static AccessScope Access(this ClaimsPrincipal user)
    {
        var role = Enum.TryParse<UserRole>(user.FindFirstValue(ClaimTypes.Role), out var r) ? r : UserRole.Viewer;
        var departments = user.FindFirstValue(DepartmentsClaim);
        return new AccessScope(user.Identity?.Name ?? "", role,
            departments is null or "*" ? null : departments.Split(',', StringSplitOptions.RemoveEmptyEntries)
                .Select(x => int.Parse(x, CultureInfo.InvariantCulture)).ToArray());
    }
}

/// <summary>Автор действий для журнала — вошедший пользователь.</summary>
public sealed class HttpActor(IHttpContextAccessor accessor) : ICurrentActor
{
    public string Name => accessor.HttpContext?.User.Identity?.Name is { Length: > 0 } name ? name : BuiltInAdmin.Login;
}

/// <summary>
/// Вход в интерфейс по логину и паролю. Встроенный «admin» — пароль из окружения (ADMIN_PASSWORD); остальные
/// пользователи — в базе, пароль как PBKDF2-хеш. Сессия — HttpOnly cookie с SameSite=Strict; роль, отделы и отметка
/// безопасности лежат в ней, а отметка сверяется с базой: блокировка или смена роли закрывает сессию за секунды.
/// </summary>
public static partial class AdminAuth
{
    public const string LoginPolicy = "login";
    private static readonly TimeSpan StampCheck = TimeSpan.FromSeconds(10);

    [GeneratedRegex("^[a-z0-9._-]{1,32}$")]
    private static partial Regex SafeLogin();

    public static IServiceCollection AddAdminAuth(this IServiceCollection services, IConfiguration configuration)
    {
        services.AddOptions<AdminOptions>()
            .Bind(configuration.GetSection(AdminOptions.Section))
            .Validate(o => o.Password.Length >= 12, "Admin:Password должен быть не короче 12 символов")
            .Validate(o => o.GuestRole.Length == 0 || o.Guest is not null,
                "Admin:GuestRole — пусто (гостевой вход выключен), Viewer или Manager")
            .ValidateOnStart();

        services.AddHttpContextAccessor();
        services.AddMemoryCache();
        // раньше AddApplication: там действующий по умолчанию «системный» автор добавляется, только если другого нет
        services.AddScoped<ICurrentActor, HttpActor>();

        services.AddAuthentication(CookieAuthenticationDefaults.AuthenticationScheme)
            .AddCookie(options =>
            {
                options.Cookie.Name = "eb_admin";
                options.Cookie.HttpOnly = true;
                options.Cookie.SameSite = SameSiteMode.Strict;
                options.Cookie.SecurePolicy = CookieSecurePolicy.SameAsRequest;
                options.ExpireTimeSpan = TimeSpan.FromHours(8);
                options.SlidingExpiration = true;
                options.Events.OnValidatePrincipal = ValidateAsync;
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
        services.AddAuthorizationBuilder()
            .AddPolicy(Policies.Manager, p => p.RequireRole(nameof(UserRole.Manager), nameof(UserRole.Admin)))
            .AddPolicy(Policies.Admin, p => p.RequireRole(nameof(UserRole.Admin)));
        return services;
    }

    public static IEndpointRouteBuilder MapAuthEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/auth").WithTags("Вход");
        group.MapPost("/login", Login).RequireRateLimiting(LoginPolicy);
        // гостевой вход без пароля: включён ли и с какими правами — чтобы страница входа показала кнопку
        group.MapGet("/options", (IOptions<AdminOptions> options) => Results.Ok(new { guest = options.Value.Guest }));
        group.MapPost("/guest", Guest).RequireRateLimiting(LoginPolicy);
        // приведение к Delegate: иначе метод с одним HttpContext считается RequestDelegate и результат теряется
        group.MapPost("/logout", (Delegate)Logout);
        // кто вошёл, его роль и отделы; demo — включён ли демо-режим (значок и тестовый стенд в интерфейсе)
        group.MapGet("/me", (HttpContext context, IOptions<Endpoints.DemoOptions> demo) =>
        {
            if (context.User.Identity?.IsAuthenticated != true)
            {
                return Results.Unauthorized();
            }

            var access = context.User.Access();
            return Results.Ok(new
            {
                login = access.Login,
                name = context.User.FindFirstValue(Policies.DisplayNameClaim) ?? access.Login,
                role = access.Role,
                departments = access.Departments,
                demo = demo.Value.Enabled,
            });
        });
        return app;
    }

    /// <summary>Отметка встроенного администратора — от его пароля: сменили ADMIN_PASSWORD — старые сессии не действуют.</summary>
    private static string AdminStamp(string password) =>
        "env-" + Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes("eb-stamp:" + password)), 0, 8);

    private static async Task ValidateAsync(CookieValidatePrincipalContext context)
    {
        var login = context.Principal?.Identity?.Name;
        var stamp = context.Principal?.FindFirstValue(Policies.StampClaim);
        var services = context.HttpContext.RequestServices;
        string? expected;
        if (login == BuiltInAdmin.Login)
        {
            expected = AdminStamp(services.GetRequiredService<IOptions<AdminOptions>>().Value.Password);
        }
        else if (login == BuiltInAdmin.GuestLogin)
        {
            // гостевой вход выключили или сменили права — гостевые сессии больше не действуют
            expected = services.GetRequiredService<IOptions<AdminOptions>>().Value.Guest is { } role ? GuestStamp(role) : null;
            // песочница гостя: удалена (гость долго отсутствовал) — сессия тоже заканчивается; жива — отмечаем, что гость здесь
            if (expected is not null && SandboxOf(context.Principal!) is { } sandbox)
            {
                var cache = services.GetRequiredService<IMemoryCache>();
                var exists = await cache.GetOrCreateAsync("guest-sandbox:" + sandbox, entry =>
                {
                    entry.AbsoluteExpirationRelativeToNow = StampCheck;
                    return services.GetRequiredService<IBalancerDbContext>().Departments
                        .AnyAsync(d => d.Id == sandbox && d.IsGuest, context.HttpContext.RequestAborted);
                });
                if (exists)
                {
                    services.GetRequiredService<Endpoints.GuestActivity>().Touch(sandbox, DateTimeOffset.UtcNow);
                }
                else
                {
                    expected = null;
                }
            }
        }
        else
        {
            // сверка с базой не чаще раза в 10 секунд на пользователя
            var cache = services.GetRequiredService<IMemoryCache>();
            expected = login is null ? null : await cache.GetOrCreateAsync("stamp:" + login, entry =>
            {
                entry.AbsoluteExpirationRelativeToNow = StampCheck;
                return services.GetRequiredService<UserService>().StampAsync(login, context.HttpContext.RequestAborted);
            });
        }

        if (expected is null || stamp is null
            || !CryptographicOperations.FixedTimeEquals(Encoding.UTF8.GetBytes(expected), Encoding.UTF8.GetBytes(stamp)))
        {
            context.RejectPrincipal();
            await context.HttpContext.SignOutAsync(CookieAuthenticationDefaults.AuthenticationScheme);
        }
    }

    private static async Task<IResult> Login(LoginRequest request, HttpContext context, IOptions<AdminOptions> options,
        UserService users, IMemoryCache cache, IBalancerDbContext db, CancellationToken ct)
    {
        var login = string.IsNullOrWhiteSpace(request.Login) ? BuiltInAdmin.Login : request.Login.Trim().ToLowerInvariant();
        var password = request.Password ?? "";
        ClaimsIdentity? identity = null;
        if (login == BuiltInAdmin.Login)
        {
            // сравниваем хеши одинаковой длины: время проверки не выдаёт даже длину пароля
            var expected = SHA256.HashData(Encoding.UTF8.GetBytes(options.Value.Password));
            var provided = SHA256.HashData(Encoding.UTF8.GetBytes(password));
            if (CryptographicOperations.FixedTimeEquals(provided, expected))
            {
                identity = Identity(login, "Администратор", UserRole.Admin, [], AdminStamp(options.Value.Password));
            }
        }
        else if (password.Length <= PasswordHasher.MaxLength
                 && await users.AuthenticateAsync(login, password, ct) is { } user)
        {
            identity = Identity(user.Login, user.DisplayName, user.Role, user.DepartmentIds, user.SecurityStamp);
            cache.Remove("stamp:" + user.Login);
        }

        db.AuditEntries.Add(new AuditEntry
        {
            // в журнал — только безопасная форма логина: произвольный ввод туда не попадает
            Actor = SafeLogin().IsMatch(login) ? login : "?",
            Action = identity is not null ? "login" : "login_failed",
            Entity = "session",
            EntityId = context.Connection.RemoteIpAddress?.ToString() ?? "",
            CreatedAt = DateTimeOffset.UtcNow,
        });
        await db.SaveChangesAsync(ct);

        if (identity is null)
        {
            return Results.Problem(statusCode: StatusCodes.Status401Unauthorized, title: "Неверный логин или пароль");
        }

        await context.SignInAsync(CookieAuthenticationDefaults.AuthenticationScheme, new ClaimsPrincipal(identity));
        return Results.NoContent();
    }

    private static string GuestStamp(UserRole role) => "guest-" + role;

    /// <summary>Песочница гостя — единственный отдел в его сессии; у наблюдателя-гостя её нет (видит все отделы).</summary>
    private static int? SandboxOf(ClaimsPrincipal user) =>
        user.Identity?.Name == BuiltInAdmin.GuestLogin && user.Access().Departments is { Count: 1 } departments
            ? departments.First()
            : null;

    /// <summary>
    /// Вход гостем: без пароля, с правами из настроек сервера (GUEST_ACCESS в .env). Наблюдатель смотрит все отделы;
    /// руководитель в демо-режиме получает свою песочницу — отдел, который видит только он. Пишется в журнал.
    /// </summary>
    private static async Task<IResult> Guest(HttpContext context, IOptions<AdminOptions> options,
        IOptions<Endpoints.DemoOptions> demo, IBalancerDbContext db, CancellationToken ct)
    {
        if (options.Value.Guest is not { } role)
        {
            return Results.Problem(statusCode: StatusCodes.Status404NotFound, title: "Гостевой вход выключен");
        }

        int[] departments = [];
        if (role == UserRole.Manager && demo.Value.Enabled)
        {
            try
            {
                departments = [await Endpoints.GuestSandbox.CreateAsync(context.RequestServices, ct)];
            }
            catch (ConfigurationConflictException ex)
            {
                return Results.Problem(statusCode: StatusCodes.Status503ServiceUnavailable, title: ex.Message);
            }
        }

        db.AuditEntries.Add(new AuditEntry
        {
            Actor = BuiltInAdmin.GuestLogin,
            Action = "login",
            Entity = "session",
            EntityId = context.Connection.RemoteIpAddress?.ToString() ?? "",
            CreatedAt = DateTimeOffset.UtcNow,
        });
        await db.SaveChangesAsync(ct);
        await context.SignInAsync(CookieAuthenticationDefaults.AuthenticationScheme,
            new ClaimsPrincipal(Identity(BuiltInAdmin.GuestLogin, "Гость", role, departments, GuestStamp(role))));
        return Results.NoContent();
    }

    private static ClaimsIdentity Identity(string login, string name, UserRole role, int[] departments, string stamp) =>
        new(
        [
            new Claim(ClaimTypes.Name, login),
            new Claim(Policies.DisplayNameClaim, name),
            new Claim(ClaimTypes.Role, role.ToString()),
            // администратор видит все отделы; остальные — перечисленные (пусто — все)
            new Claim(Policies.DepartmentsClaim, role == UserRole.Admin || departments.Length == 0
                ? "*"
                : string.Join(',', departments.Select(d => d.ToString(CultureInfo.InvariantCulture)))),
            new Claim(Policies.StampClaim, stamp),
        ], CookieAuthenticationDefaults.AuthenticationScheme);

    private static async Task<IResult> Logout(HttpContext context)
    {
        // гость ушёл — его песочница больше не нужна
        if (SandboxOf(context.User) is { } sandbox)
        {
            await Endpoints.GuestSandbox.DeleteAsync(context.RequestServices, sandbox, context.RequestAborted);
        }

        await context.SignOutAsync(CookieAuthenticationDefaults.AuthenticationScheme);
        return Results.NoContent();
    }
}
