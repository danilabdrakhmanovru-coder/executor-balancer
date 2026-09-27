namespace ExecutorBalancer.Domain;

/// <summary>Роль пользователя интерфейса. Права растут по порядку: наблюдатель ⊂ руководитель ⊂ администратор.</summary>
public enum UserRole
{
    /// <summary>Смотрит мониторинг, заявки, сотрудников и отчёты своих отделов; ничего не меняет.</summary>
    Viewer,

    /// <summary>Руководитель отдела: плюс работа с сотрудниками (загрузка из файла, «больше нормы»),
    /// запуск ИИ-разбора и журнал своих отделов. Настройки видит, но не меняет.</summary>
    Manager,

    /// <summary>Всё: параметры и правила, мотивация, отделы, пользователи, тестовый стенд.</summary>
    Admin,
}

/// <summary>
/// Пользователь интерфейса. Встроенный администратор «admin» живёт в окружении (ADMIN_PASSWORD) и в базе не хранится —
/// им нельзя заблокировать вход. Остальных заводит администратор; пароль хранится только как PBKDF2-хеш.
/// </summary>
public class User
{
    public int Id { get; set; }

    public string Login { get; set; } = "";

    public string DisplayName { get; set; } = "";

    public string PasswordHash { get; set; } = "";

    public UserRole Role { get; set; }

    /// <summary>Отделы, которые видит пользователь. Пусто — все отделы (для администратора всегда все).</summary>
    public int[] DepartmentIds { get; set; } = [];

    public bool IsActive { get; set; } = true;

    /// <summary>Меняется при смене роли, отделов, пароля или блокировке — открытые сессии со старой отметкой закрываются.</summary>
    public string SecurityStamp { get; set; } = "";

    public DateTimeOffset CreatedAt { get; set; }

    public DateTimeOffset? LastLoginAt { get; set; }
}
