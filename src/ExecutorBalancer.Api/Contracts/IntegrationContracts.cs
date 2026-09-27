using System.Text.Json;
using ExecutorBalancer.Application.Rules;
using ExecutorBalancer.Domain;

namespace ExecutorBalancer.Api.Contracts;

public sealed record OrderRequest(long Id, long? ParentId, string? Status, Dictionary<string, JsonElement>? Attributes);

public sealed record StatusRequest(string? Status);

public sealed record ExecutorRequest(
    string? FullName,
    bool IsActive,
    int? DailyLimit,
    decimal? QualificationWeight,
    Dictionary<string, JsonElement>? Attributes,
    int? ExtraPercent = null);

/// <summary>Проверки формы запроса. Типы параметров проверяются позже — по справочнику полей.</summary>
public static class RequestValidation
{
    public static Dictionary<string, string[]> Validate(OrderRequest request)
    {
        var errors = new Dictionary<string, string[]>();
        if (request.Id <= 0)
        {
            errors["id"] = ["должен быть положительным"];
        }

        if (request.ParentId is <= 0)
        {
            errors["parentId"] = ["должен быть положительным"];
        }
        else if (request.ParentId == request.Id)
        {
            errors["parentId"] = ["заявка не может быть родителем самой себе"];
        }

        if (request.Status is not null && !TryParseStatus(request.Status, out _))
        {
            errors["status"] = ["допустимо: processed, await, accept, reject"];
        }

        ValidateAttributes(request.Attributes, errors);
        return errors;
    }

    public static Dictionary<string, string[]> Validate(ExecutorRequest request)
    {
        var errors = new Dictionary<string, string[]>();
        if (string.IsNullOrWhiteSpace(request.FullName) || request.FullName.Length > 300)
        {
            errors["fullName"] = ["обязательно, не длиннее 300 символов"];
        }

        if (request.DailyLimit is < 0 or > 100_000)
        {
            errors["dailyLimit"] = ["от 0 до 100000 или null — без лимита"];
        }

        if (request.ExtraPercent is < 0 or > 100)
        {
            errors["extraPercent"] = ["от 0 до 100 процентов сверх нормы; 0 — режим выключен"];
        }

        if (request.QualificationWeight is < 0.1m or > 100m)
        {
            errors["qualificationWeight"] = ["от 0.1 до 100"];
        }

        ValidateAttributes(request.Attributes, errors);
        return errors;
    }

    public static bool TryParseStatus(string? value, out OrderStatus status)
    {
        status = default;
        return value is not null
               && !int.TryParse(value, out _)
               && Enum.TryParse(value, ignoreCase: true, out status)
               && Enum.IsDefined(status);
    }

    public static void ValidateAttributes(Dictionary<string, JsonElement>? attributes, Dictionary<string, string[]> errors)
    {
        if (attributes is null)
        {
            return;
        }

        if (attributes.Count > FieldCatalog.MaxAttributes)
        {
            errors["attributes"] = [$"не больше {FieldCatalog.MaxAttributes} параметров"];
            return;
        }

        var badKeys = attributes.Keys.Where(k => !FieldCatalog.IsValidKey(k)).Take(5).ToList();
        if (badKeys.Count > 0)
        {
            errors["attributes"] = [$"некорректные ключи: {string.Join(", ", badKeys)}"];
        }
    }
}
