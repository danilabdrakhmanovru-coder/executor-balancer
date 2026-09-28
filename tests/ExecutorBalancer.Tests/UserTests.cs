using ExecutorBalancer.Application;
using ExecutorBalancer.Application.Users;
using ExecutorBalancer.Domain;
using Microsoft.EntityFrameworkCore;

namespace ExecutorBalancer.Tests;

/// <summary>Пользователи и роли: проверка ввода, хранение пароля, вход, закрытие сессий, доступ к отделам.</summary>
public class UserTests : IAsyncLifetime
{
    private BalancerFixture _f = null!;

    public async Task InitializeAsync() => _f = await BalancerFixture.CreateAsync();

    public async Task DisposeAsync() => await _f.DisposeAsync();

    private static UserInput Manager(string login = "petrova", string password = "Надёжный-пароль-1", int[]? departments = null) =>
        new(login, "Петрова А. В.", password, UserRole.Manager, departments ?? [D], true);

    [Fact]
    public async Task PasswordIsStoredOnlyAsHash()
    {
        var created = await _f.Users(u => u.CreateAsync(Manager(), CancellationToken.None));

        var stored = await _f.Query(db => db.Users.AsNoTracking().SingleAsync(u => u.Id == created.Id));
        Assert.StartsWith("pbkdf2-sha256$600000$", stored.PasswordHash, StringComparison.Ordinal);
        Assert.DoesNotContain("Надёжный", stored.PasswordHash, StringComparison.Ordinal);
        Assert.True(PasswordHasher.Verify("Надёжный-пароль-1", stored.PasswordHash));
        Assert.False(PasswordHasher.Verify("надёжный-пароль-1", stored.PasswordHash));
        Assert.False(PasswordHasher.Verify("x", "pbkdf2-sha256$1$AAAA$AAAA")); // испорченная строка — просто «не совпало»
    }

    [Fact]
    public async Task LoginChecksPasswordAndBlock()
    {
        var created = await _f.Users(u => u.CreateAsync(Manager(), CancellationToken.None));

        Assert.NotNull(await _f.Users(u => u.AuthenticateAsync(" Petrova ", "Надёжный-пароль-1", CancellationToken.None)));
        Assert.Null(await _f.Users(u => u.AuthenticateAsync("petrova", "неверный-пароль", CancellationToken.None)));
        Assert.Null(await _f.Users(u => u.AuthenticateAsync("nobody", "Надёжный-пароль-1", CancellationToken.None)));

        var stamp = await _f.Users(u => u.StampAsync("petrova", CancellationToken.None));
        await _f.Users(u => u.UpdateAsync(created.Id, Manager() with { Password = null, IsActive = false }, CancellationToken.None));

        Assert.Null(await _f.Users(u => u.AuthenticateAsync("petrova", "Надёжный-пароль-1", CancellationToken.None)));
        Assert.Null(await _f.Users(u => u.StampAsync("petrova", CancellationToken.None))); // открытые сессии закроются
        Assert.NotNull(stamp);
    }

    [Fact]
    public async Task RoleChangeInvalidatesSessionsAndIsAudited()
    {
        var created = await _f.Users(u => u.CreateAsync(Manager(), CancellationToken.None));
        var before = await _f.Users(u => u.StampAsync("petrova", CancellationToken.None));

        await _f.Users(u => u.UpdateAsync(created.Id, Manager(password: "Другой-пароль-22") with { Role = UserRole.Viewer },
            CancellationToken.None));

        Assert.NotEqual(before, await _f.Users(u => u.StampAsync("petrova", CancellationToken.None)));
        var audit = await _f.Query(db => db.AuditEntries.AsNoTracking().Where(a => a.Entity == "user").ToListAsync());
        Assert.Equal(["user_created", "user_updated"], audit.OrderBy(a => a.Id).Select(a => a.Action));
        Assert.All(audit, a => Assert.DoesNotContain("пароль", a.DataJson, StringComparison.OrdinalIgnoreCase));
        Assert.All(audit, a => Assert.Equal(BuiltInAdmin.Login, a.Actor));
    }

    [Theory]
    [InlineData("admin", "Надёжный-пароль-1", "login")]
    [InlineData("guest", "Надёжный-пароль-1", "login")]
    [InlineData("A", "Надёжный-пароль-1", "login")]
    [InlineData("петрова", "Надёжный-пароль-1", "login")]
    [InlineData("petrova", "короткий", "password")]
    [InlineData("petrova", "aaaaaaaaaaaa", "password")]
    public async Task InvalidInputIsRejected(string login, string password, string field)
    {
        var error = await Assert.ThrowsAsync<InvalidInputException>(() =>
            _f.Users(u => u.CreateAsync(Manager(login, password), CancellationToken.None)));

        Assert.Contains(field, error.Errors.Keys);
    }

    [Fact]
    public async Task UnknownDepartmentAndDuplicateLoginAreRejected()
    {
        await Assert.ThrowsAsync<InvalidInputException>(() =>
            _f.Users(u => u.CreateAsync(Manager(departments: [999]), CancellationToken.None)));
        await _f.Users(u => u.CreateAsync(Manager(), CancellationToken.None));
        await Assert.ThrowsAsync<ExecutorBalancer.Application.Configuration.ConfigurationConflictException>(() =>
            _f.Users(u => u.CreateAsync(Manager(login: "PETROVA"), CancellationToken.None)));
    }

    [Fact]
    public void AccessScopeLimitsDepartments()
    {
        var manager = new AccessScope("petrova", UserRole.Manager, [1, 3]);
        var everywhere = new AccessScope("ivanov", UserRole.Viewer, null);
        var admin = new AccessScope("admin", UserRole.Admin, [1]);

        Assert.True(manager.CanSee(3));
        Assert.False(manager.CanSee(2));
        Assert.True(everywhere.CanSee(2));
        Assert.True(admin.CanSee(2)); // администратор видит все отделы
        Assert.True(manager.AtLeast(UserRole.Viewer));
        Assert.False(manager.AtLeast(UserRole.Admin));
    }
}
