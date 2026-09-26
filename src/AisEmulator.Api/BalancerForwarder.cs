using System.Net;
using System.Net.Http.Json;
using System.Threading.Channels;

namespace AisEmulator.Api;

public sealed record ForwardCommand(HttpMethod Method, string Path, object Body, int Attempt = 0);

/// <summary>
/// Отправка событий АИС в балансировщик: очередь в памяти, 4 параллельных отправителя,
/// повтор с растущей задержкой, если балансировщик недоступен.
/// </summary>
public sealed class BalancerForwarder(IHttpClientFactory httpClients, ILogger<BalancerForwarder> logger)
    : BackgroundService
{
    public const string HttpClientName = "balancer";
    private const int MaxAttempts = 30;

    private readonly Channel<ForwardCommand> _queue = Channel.CreateUnbounded<ForwardCommand>();
    private long _delivered;
    private long _dropped;

    public int Backlog => _queue.Reader.Count;
    public long Delivered => Interlocked.Read(ref _delivered);
    public long Dropped => Interlocked.Read(ref _dropped);

    public void Enqueue(ForwardCommand command) => _queue.Writer.TryWrite(command);

    protected override Task ExecuteAsync(CancellationToken stoppingToken) =>
        Task.WhenAll(Enumerable.Range(0, 4).Select(_ => ConsumeAsync(stoppingToken)));

    private async Task ConsumeAsync(CancellationToken stoppingToken)
    {
        var client = httpClients.CreateClient(HttpClientName);
        await foreach (var command in _queue.Reader.ReadAllAsync(stoppingToken))
        {
            try
            {
                using var request = new HttpRequestMessage(command.Method, command.Path)
                {
                    Content = JsonContent.Create(command.Body),
                };
                using var response = await client.SendAsync(request, stoppingToken);
                if (response.IsSuccessStatusCode)
                {
                    Interlocked.Increment(ref _delivered);
                    continue;
                }

                // ошибка в данных — повтор не поможет; 404 и 429 могут пройти позже
                var code = (int)response.StatusCode;
                if (code is >= 400 and < 500
                    && response.StatusCode is not HttpStatusCode.NotFound and not HttpStatusCode.TooManyRequests)
                {
                    Interlocked.Increment(ref _dropped);
                    logger.LogWarning("Балансировщик отклонил {Method} {Path}: HTTP {Code}", command.Method, command.Path, code);
                    continue;
                }
            }
            catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException
                                       && !stoppingToken.IsCancellationRequested)
            {
                logger.LogDebug(ex, "Балансировщик недоступен, повторим");
            }

            ScheduleRetry(command);
        }
    }

    private void ScheduleRetry(ForwardCommand command)
    {
        if (command.Attempt >= MaxAttempts)
        {
            Interlocked.Increment(ref _dropped);
            return;
        }

        var delay = TimeSpan.FromSeconds(Math.Min(30, Math.Pow(2, command.Attempt)));
        _ = Task.Delay(delay).ContinueWith(
            _ => _queue.Writer.TryWrite(command with { Attempt = command.Attempt + 1 }),
            TaskScheduler.Default);
    }
}
