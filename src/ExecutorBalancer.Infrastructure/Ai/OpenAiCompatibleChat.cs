using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using ExecutorBalancer.Application.Insights;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace ExecutorBalancer.Infrastructure.Ai;

public sealed class AiOptions
{
    public const string Section = "Ai";
    public const string HttpClientName = "ai";

    /// <summary>
    /// Адрес OpenAI-совместимого API, без «/chat/completions»: облачная модель или своя в контуре заказчика
    /// (например, Ollama — http://host:11434/v1). Пусто — ИИ-разбор выключен.
    /// </summary>
    public string BaseUrl { get; set; } = "";

    public string Model { get; set; } = "";

    /// <summary>Ключ API — только из окружения (AI_API_KEY). Для своей модели без ключа — пусто.</summary>
    public string ApiKey { get; set; } = "";

    public int TimeoutSeconds { get; set; } = 90;

    public int MaxTokens { get; set; } = 1500;
}

/// <summary>
/// Вызов модели через POST {BaseUrl}/chat/completions — формат, который понимают большинство облачных и локальных
/// моделей. В модель уходит только обезличенная сводка (см. <see cref="AiAnalyst"/>); ответ читается как текст.
/// </summary>
public sealed class OpenAiCompatibleChat(IHttpClientFactory http, IOptions<AiOptions> options, ILogger<OpenAiCompatibleChat> logger)
    : IAiChat
{
    private const int MaxResponseBytes = 256 * 1024;

    public bool IsConfigured => !string.IsNullOrWhiteSpace(options.Value.BaseUrl) && !string.IsNullOrWhiteSpace(options.Value.Model);

    public string Model => options.Value.Model;

    public async Task<string> CompleteAsync(string system, string user, CancellationToken cancellationToken)
    {
        var settings = options.Value;
        var client = http.CreateClient(AiOptions.HttpClientName);
        using var request = new HttpRequestMessage(HttpMethod.Post, "chat/completions")
        {
            // тело целиком, с длиной: часть серверов моделей и прокси не принимает потоковую (chunked) передачу
            Content = new StringContent(JsonSerializer.Serialize(new
            {
                model = settings.Model,
                temperature = 0.2,
                max_tokens = settings.MaxTokens,
                messages = new object[]
                {
                    new { role = "system", content = system },
                    new { role = "user", content = user },
                },
            }), Encoding.UTF8, "application/json"),
        };
        if (!string.IsNullOrWhiteSpace(settings.ApiKey))
        {
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", settings.ApiKey);
        }

        HttpResponseMessage response;
        try
        {
            response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException && !cancellationToken.IsCancellationRequested)
        {
            logger.LogWarning(ex, "ИИ: модель не ответила");
            throw new AiUnavailableException("модель не ответила — проверьте адрес и сеть (AI_BASE_URL)");
        }

        using (response)
        {
            if (!response.IsSuccessStatusCode)
            {
                // тело ответа не показываем: в нём бывают подробности аккаунта
                logger.LogWarning("ИИ: модель ответила HTTP {Status}", (int)response.StatusCode);
                throw new AiUnavailableException((int)response.StatusCode switch
                {
                    401 or 403 => "модель отклонила ключ — проверьте AI_API_KEY",
                    404 => "не найдена модель или адрес — проверьте AI_MODEL и AI_BASE_URL",
                    429 => "превышен лимит запросов к модели — попробуйте позже",
                    _ => $"модель ответила ошибкой HTTP {(int)response.StatusCode}",
                });
            }

            if (response.Content.Headers.ContentLength > MaxResponseBytes)
            {
                throw new AiUnavailableException("слишком длинный ответ модели");
            }

            try
            {
                await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
                using var limited = new MemoryStream();
                var buffer = new byte[16 * 1024];
                int read;
                while ((read = await stream.ReadAsync(buffer, cancellationToken)) > 0)
                {
                    if (limited.Length + read > MaxResponseBytes)
                    {
                        throw new AiUnavailableException("слишком длинный ответ модели");
                    }

                    limited.Write(buffer, 0, read);
                }

                using var json = JsonDocument.Parse(limited.ToArray());
                var content = json.RootElement.GetProperty("choices")[0].GetProperty("message").GetProperty("content").GetString();
                return string.IsNullOrWhiteSpace(content) ? throw new AiUnavailableException("модель вернула пустой ответ") : content;
            }
            catch (Exception ex) when (ex is JsonException or KeyNotFoundException or IndexOutOfRangeException or InvalidOperationException)
            {
                logger.LogWarning(ex, "ИИ: ответ не в формате chat/completions");
                throw new AiUnavailableException("ответ модели не в формате chat/completions");
            }
        }
    }
}
