using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using ExecutorBalancer.Application.Configuration;
using ExecutorBalancer.Domain;
using Microsoft.EntityFrameworkCore;

namespace ExecutorBalancer.Application.Users;

/// <summary>Кто выполняет действие — пишется в журнал. В интерфейсе это вошедший пользователь, в фоне — «система».</summary>
public interface ICurrentActor
{
    string Name { get; }
}

/// <summary>Действие без вошедшего пользователя (фоновые задачи, тесты) — в журнале «admin», как было до ролей.</summary>
public sealed class SystemActor : ICurrentActor
{
    public string Name => BuiltInAdmin.Login;
}

public static class BuiltInAdmin
{
    /// <summary>Встроенный администратор: пароль — ADMIN_PASSWORD из окружения, в базе не хранится.</summary>
    public const string Login = "admin";

    /// <summary>Гостевой вход без пароля (кнопка на странице входа, если он включён на сервере). Логин зарезервирован.</summary>
    public const string GuestLogin = "guest";
}

/// <summary>Что доступно пользователю: роль и отделы (null — все).</summary>
public sealed record AccessScope(string Login, UserRole Role, IReadOnlyCollection<int>? Departments)
{
    public bool AtLeast(UserRole role) => Role >= role;

    public bool CanSee(int departmentId) => Role == UserRole.Admin || Departments is null || Departments.Contains(departmentId);

    /// <summary>
    /// Песочницы гостей видят администратор и сам гость (у него в списке только его песочница). Пользователь «все отделы»
    /// их не видит: это чужие демонстрации, а не отделы компании.
    /// </summary>
    public bool SeesGuestSandboxes => Role == UserRole.Admin || Departments is not null;
}

/// <summary>PBKDF2-SHA256 с солью: формат «pbkdf2-sha256$итерации$соль$хеш» (Base64).</summary>
public static class PasswordHasher
{
    public const int MinLength = 10;
    public const int MaxLength = 128;
    private const int Iterations = 600_000; // рекомендация OWASP для PBKDF2-SHA256
    private const int SaltSize = 16;
    private const int HashSize = 32;
    private const string Scheme = "pbkdf2-sha256";

    public static string Hash(string password)
    {
        var salt = RandomNumberGenerator.GetBytes(SaltSize);
        var hash = Rfc2898DeriveBytes.Pbkdf2(Encoding.UTF8.GetBytes(password), salt, Iterations, HashAlgorithmName.SHA256, HashSize);
        return string.Join('$', Scheme, Iterations.ToString(CultureInfo.InvariantCulture), Convert.ToBase64String(salt),
            Convert.ToBase64String(hash));
    }

    /// <summary>Проверка за постоянное время. Испорченная строка хеша — просто «не совпало».</summary>
    public static bool Verify(string password, string stored)
    {
        var parts = stored.Split('$');
        if (parts.Length != 4 || parts[0] != Scheme
            || !int.TryParse(parts[1], NumberStyles.None, CultureInfo.InvariantCulture, out var iterations)
            || iterations is < 10_000 or > 5_000_000)
        {
            return false;
        }

        try
        {
            var salt = Convert.FromBase64String(parts[2]);
            var expected = Convert.FromBase64String(parts[3]);
            var actual = Rfc2898DeriveBytes.Pbkdf2(Encoding.UTF8.GetBytes(password), salt, iterations, HashAlgorithmName.SHA256,
                expected.Length);
            return CryptographicOperations.FixedTimeEquals(actual, expected);
        }
        catch (FormatException)
        {
            return false;
        }
    }

    /// <summary>
    /// Хеш-пустышка для несуществующего логина: проверка занимает столько же времени, и по времени ответа
    /// нельзя понять, есть ли такой пользователь.
    /// </summary>
    public static readonly string Dummy = Hash("dummy-password-for-timing");
}

public sealed record UserInput(string? Login, string? DisplayName, string? Password, UserRole? Role, int[]? DepartmentIds,
    bool? IsActive);

public sealed record UserView(int Id, string Login, string DisplayName, UserRole Role, int[] DepartmentIds, bool IsActive,
    DateTimeOffset CreatedAt, DateTimeOffset? LastLoginAt);

/// <summary>Пользователи интерфейса: заводит и меняет только администратор, каждое изменение — в журнал.</summary>
public sealed partial class UserService(IBalancerDbContext db, ICurrentActor actor, TimeProvider clock)
{
    public const int MaxUsers = 200;

    private static readonly JsonSerializerOptions AuditJson = new(JsonSerializerDefaults.Web)
    {
        Converters = { new JsonStringEnumConverter() },
    };

    [GeneratedRegex("^[a-z][a-z0-9._-]{2,31}$")]
    private static partial Regex LoginPattern();

    public async Task<IReadOnlyList<UserView>> ListAsync(CancellationToken cancellationToken) =>
        (await db.Users.AsNoTracking().OrderBy(u => u.Login).ToListAsync(cancellationToken)).Select(View).ToList();

    /// <summary>Вход: пользователь, если логин и пароль верны и он не заблокирован.</summary>
    public async Task<User?> AuthenticateAsync(string login, string password, CancellationToken cancellationToken)
    {
        var normalized = login.Trim().ToLowerInvariant();
        var user = await db.Users.FirstOrDefaultAsync(u => u.Login == normalized, cancellationToken);
        // проверяем пароль и для несуществующего логина — время ответа одинаковое
        var ok = PasswordHasher.Verify(password, user?.PasswordHash ?? PasswordHasher.Dummy);
        if (user is null || !ok || !user.IsActive)
        {
            return null;
        }

        user.LastLoginAt = clock.GetUtcNow();
        await db.SaveChangesAsync(cancellationToken);
        return user;
    }

    /// <summary>Действующая отметка безопасности или null, если пользователя нет или он заблокирован.</summary>
    public Task<string?> StampAsync(string login, CancellationToken cancellationToken) =>
        db.Users.AsNoTracking().Where(u => u.Login == login && u.IsActive).Select(u => u.SecurityStamp)
            .FirstOrDefaultAsync(cancellationToken);

    public async Task<UserView> CreateAsync(UserInput input, CancellationToken cancellationToken)
    {
        var errors = new Dictionary<string, string[]>();
        var login = input.Login?.Trim().ToLowerInvariant() ?? "";
        if (!LoginPattern().IsMatch(login))
        {
            errors["login"] = ["логин: 3–32 символа — латинские буквы, цифры, «.», «_», «-», начинается с буквы"];
        }
        else if (login is BuiltInAdmin.Login or BuiltInAdmin.GuestLogin)
        {
            errors["login"] = [$"логин «{login}» зарезервирован"];
        }

        var name = Name(input.DisplayName, errors);
        Password(input.Password, errors);
        var role = Role(input.Role, errors);
        var departments = await DepartmentsAsync(input.DepartmentIds, errors, cancellationToken);
        ThrowIfAny(errors);

        if (await db.Users.CountAsync(cancellationToken) >= MaxUsers)
        {
            throw new ConfigurationConflictException($"пользователей не больше {MaxUsers}");
        }

        if (await db.Users.AnyAsync(u => u.Login == login, cancellationToken))
        {
            throw new ConfigurationConflictException("пользователь с таким логином уже есть");
        }

        var user = new User
        {
            Login = login,
            DisplayName = name,
            PasswordHash = PasswordHasher.Hash(input.Password!),
            Role = role,
            DepartmentIds = departments,
            IsActive = input.IsActive ?? true,
            SecurityStamp = NewStamp(),
            CreatedAt = clock.GetUtcNow(),
        };
        db.Users.Add(user);
        Audit("user_created", user, null, Snapshot(user));
        await db.SaveChangesAsync(cancellationToken);
        return View(user);
    }

    /// <summary>Роль, отделы, имя, блокировка и (если задан) новый пароль. Сессии пользователя после этого закрываются.</summary>
    public async Task<UserView?> UpdateAsync(int id, UserInput input, CancellationToken cancellationToken)
    {
        var user = await db.Users.FirstOrDefaultAsync(u => u.Id == id, cancellationToken);
        if (user is null)
        {
            return null;
        }

        var errors = new Dictionary<string, string[]>();
        var name = Name(input.DisplayName, errors);
        if (!string.IsNullOrEmpty(input.Password))
        {
            Password(input.Password, errors);
        }

        var role = Role(input.Role, errors);
        var departments = await DepartmentsAsync(input.DepartmentIds, errors, cancellationToken);
        ThrowIfAny(errors);

        var before = Snapshot(user);
        user.DisplayName = name;
        user.Role = role;
        user.DepartmentIds = departments;
        user.IsActive = input.IsActive ?? user.IsActive;
        var passwordChanged = !string.IsNullOrEmpty(input.Password);
        if (passwordChanged)
        {
            user.PasswordHash = PasswordHasher.Hash(input.Password!);
        }

        user.SecurityStamp = NewStamp();
        // пароль в журнал не пишется — только сам факт смены
        Audit("user_updated", user, before, new
        {
            user.Login, user.DisplayName, user.Role, user.DepartmentIds, user.IsActive, PasswordChanged = passwordChanged,
        });
        await db.SaveChangesAsync(cancellationToken);
        return View(user);
    }

    public async Task<bool> DeleteAsync(int id, CancellationToken cancellationToken)
    {
        var user = await db.Users.FirstOrDefaultAsync(u => u.Id == id, cancellationToken);
        if (user is null)
        {
            return false;
        }

        if (string.Equals(user.Login, actor.Name, StringComparison.Ordinal))
        {
            throw new ConfigurationConflictException("нельзя удалить самого себя");
        }

        db.Users.Remove(user);
        Audit("user_deleted", user, Snapshot(user), null);
        await db.SaveChangesAsync(cancellationToken);
        return true;
    }

    private void Audit(string action, User user, object? before, object? after) =>
        db.AuditEntries.Add(new AuditEntry
        {
            Actor = actor.Name,
            Action = action,
            Entity = "user",
            EntityId = user.Login,
            DataJson = JsonSerializer.Serialize(new { before, after }, AuditJson),
            CreatedAt = clock.GetUtcNow(),
        });

    private static object Snapshot(User u) => new { u.Login, u.DisplayName, u.Role, u.DepartmentIds, u.IsActive };

    private static UserView View(User u) =>
        new(u.Id, u.Login, u.DisplayName, u.Role, u.DepartmentIds, u.IsActive, u.CreatedAt, u.LastLoginAt);

    private static string NewStamp() => Convert.ToHexString(RandomNumberGenerator.GetBytes(16));

    private static string Name(string? value, Dictionary<string, string[]> errors)
    {
        var name = value?.Trim() ?? "";
        if (name.Length is 0 or > 120 || name.Any(char.IsControl))
        {
            errors["displayName"] = ["имя: от 1 до 120 символов"];
        }

        return name;
    }

    private static void Password(string? value, Dictionary<string, string[]> errors)
    {
        if (value is null || value.Length < PasswordHasher.MinLength || value.Length > PasswordHasher.MaxLength)
        {
            errors["password"] = [$"пароль: от {PasswordHasher.MinLength} до {PasswordHasher.MaxLength} символов"];
        }
        else if (value.Distinct().Count() < 5)
        {
            errors["password"] = ["пароль слишком простой: нужно хотя бы 5 разных символов"];
        }
    }

    private static UserRole Role(UserRole? value, Dictionary<string, string[]> errors)
    {
        if (value is { } role && Enum.IsDefined(role))
        {
            return role;
        }

        errors["role"] = ["роль: Viewer, Manager или Admin"];
        return UserRole.Viewer;
    }

    private async Task<int[]> DepartmentsAsync(int[]? ids, Dictionary<string, string[]> errors, CancellationToken cancellationToken)
    {
        var wanted = (ids ?? []).Distinct().Order().ToArray();
        if (wanted.Length > DepartmentService.MaxDepartments)
        {
            errors["departmentIds"] = ["слишком много отделов"];
            return [];
        }

        var known = await db.Departments.AsNoTracking().Where(d => wanted.Contains(d.Id)).CountAsync(cancellationToken);
        if (known != wanted.Length)
        {
            errors["departmentIds"] = ["неизвестный отдел"];
        }

        return wanted;
    }

    private static void ThrowIfAny(Dictionary<string, string[]> errors)
    {
        if (errors.Count > 0)
        {
            throw new InvalidInputException(errors);
        }
    }
}
