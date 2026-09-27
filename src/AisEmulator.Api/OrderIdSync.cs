using System.Net.Http.Json;

namespace AisEmulator.Api;

/// <summary>
/// Эмулятор держит заявки в памяти и после перезапуска начал бы нумеровать их с 1, а балансировщик принял бы
/// новые заявки за повторы уже известных. Поэтому при запуске (и перед первой заявкой, если балансировщик ещё
/// не отвечал) эмулятор узнаёт у балансировщика последний номер и продолжает с него.
/// </summary>
public sealed class OrderIdSync(IHttpClientFactory httpClients, AisStore store, ILogger<OrderIdSync> logger) : BackgroundService
{
    private readonly SemaphoreSlim _gate = new(1, 1);
    private volatile bool _synced;

    /// <summary>Сверить номера, если ещё не сверены; балансировщик недоступен — не ждём, повторим позже.</summary>
    public async Task EnsureAsync(CancellationToken cancellationToken)
    {
        if (_synced || !await _gate.WaitAsync(TimeSpan.FromSeconds(3), cancellationToken))
        {
            return;
        }

        try
        {
            if (_synced)
            {
                return;
            }

            var client = httpClients.CreateClient(BalancerForwarder.HttpClientName);
            var answer = await client.GetFromJsonAsync<LastIdAnswer>("api/integration/orders/last-id", cancellationToken);
            store.ContinueOrderIdsFrom((answer?.LastId ?? 0) + 1);
            _synced = true;
            logger.LogInformation("Номера заявок продолжаются после {LastId}", answer?.LastId ?? 0);
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or System.Text.Json.JsonException)
        {
            logger.LogDebug(ex, "Балансировщик пока не ответил — номера сверим позже");
        }
        finally
        {
            _gate.Release();
        }
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        // балансировщик может подняться позже эмулятора — пробуем, пока не получится
        while (!_synced && !stoppingToken.IsCancellationRequested)
        {
            await EnsureAsync(stoppingToken);
            if (!_synced)
            {
                await Task.Delay(TimeSpan.FromSeconds(3), stoppingToken);
            }
        }
    }

    public override void Dispose()
    {
        _gate.Dispose();
        base.Dispose();
    }

    private sealed record LastIdAnswer(long LastId);
}
