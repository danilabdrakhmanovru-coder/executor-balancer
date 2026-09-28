using System.Security.Claims;
using ExecutorBalancer.Application.Users;

namespace ExecutorBalancer.Api.Security;

/// <summary>Гостевая сессия: у гостя-руководителя в демо-режиме свой отдел-песочница — единственный в его сессии.</summary>
public static class GuestSession
{
    /// <summary>Песочница гостя; у наблюдателя-гостя и обычных пользователей её нет.</summary>
    public static int? SandboxOf(ClaimsPrincipal user) =>
        user.Identity?.Name == BuiltInAdmin.GuestLogin && user.Access().Departments is { Count: 1 } departments
            ? departments.First()
            : null;
}
