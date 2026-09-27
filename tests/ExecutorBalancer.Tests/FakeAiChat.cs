using ExecutorBalancer.Application.Insights;

namespace ExecutorBalancer.Tests;

/// <summary>Модель для тестов: запоминает, что ей отправили, и отвечает заданным текстом.</summary>
internal sealed class FakeAiChat : IAiChat
{
    public bool IsConfigured { get; set; }

    public string Model => "test-model";

    public string Answer { get; set; } = "";

    public List<string> Requests { get; } = [];

    public Task<string> CompleteAsync(string system, string user, CancellationToken cancellationToken)
    {
        Requests.Add(user);
        return Task.FromResult(Answer);
    }
}
