using System.Collections.Concurrent;
using System.Net;
using System.Net.Http.Json;
using ExecutorBalancer.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace ExecutorBalancer.Infrastructure.Workers;

/// <summary>
/// Доставка назначений в АИС. Сообщения забираются пачкой через FOR UPDATE SKIP LOCKED —
/// несколько экземпляров сервиса не отправят одно сообщение одновременно.
/// Порядковый номер сообщения передаётся в АИС: устаревшее назначение там не перезапишет новое.
/// </summary>
internal sealed class OutboxDispatcher(
    IServiceScopeFactory scopes,
    IHttpClientFactory httpClients,
    IOptions<AisOptions> options,
    ILogger<OutboxDispatcher> logger) : BackgroundService
{
    private const int BatchSize = 100;

    private const string ClaimSql = """
        UPDATE outbox_messages
        SET "NextAttemptAt" = now() + interval '30 seconds', "Attempts" = "Attempts" + 1
        WHERE "Id" IN (
            SELECT "Id" FROM outbox_messages
            WHERE "SentAt" IS NULL AND "NextAttemptAt" <= now()
            ORDER BY "Id"
            LIMIT 100
            FOR UPDATE SKIP LOCKED)
        RETURNING "Id", "OrderId", "ExecutorId", "Attempts"
        """;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (string.IsNullOrWhiteSpace(options.Value.BaseUrl))
        {
            logger.LogWarning("Ais:BaseUrl не задан — назначения в АИС не отправляются");
            return;
        }

        while (!stoppingToken.IsCancellationRequested)
        {
            var claimed = 0;
            try
            {
                claimed = await DispatchBatchAsync(stoppingToken);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                logger.LogError(ex, "Ошибка отправки назначений в АИС");
            }

            if (claimed < BatchSize)
            {
                await Task.Delay(options.Value.OutboxPollInterval, stoppingToken);
            }
        }
    }

    private async Task<int> DispatchBatchAsync(CancellationToken cancellationToken)
    {
        await using var scope = scopes.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<BalancerDbContext>();
        var claimed = await db.Database.SqlQueryRaw<ClaimedMessage>(ClaimSql).ToListAsync(cancellationToken);
        if (claimed.Count == 0)
        {
            return 0;
        }

        var client = httpClients.CreateClient(AisOptions.HttpClientName);
        var delivered = new ConcurrentBag<long>();
        var failed = new ConcurrentDictionary<long, string>();
        await Parallel.ForEachAsync(claimed,
            new ParallelOptions { MaxDegreeOfParallelism = 8, CancellationToken = cancellationToken },
            async (message, token) =>
            {
                try
                {
                    using var response = await client.PostAsJsonAsync("api/ais/assignments",
                        new { orderId = message.OrderId, executorId = message.ExecutorId, sequence = message.Id },
                        token);
                    // 404: такой заявки в АИС нет (создана в обход АИС) — повторять бессмысленно
                    if (response.IsSuccessStatusCode || response.StatusCode == HttpStatusCode.NotFound)
                    {
                        delivered.Add(message.Id);
                    }
                    else
                    {
                        failed[message.Id] = $"HTTP {(int)response.StatusCode}";
                    }
                }
                catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
                {
                    failed[message.Id] = ex.Message;
                }
            });

        var now = DateTimeOffset.UtcNow;
        if (!delivered.IsEmpty)
        {
            var ids = delivered.ToArray();
            await db.OutboxMessages.Where(m => ids.Contains(m.Id))
                .ExecuteUpdateAsync(s => s.SetProperty(m => m.SentAt, now), cancellationToken);
        }

        foreach (var (id, error) in failed)
        {
            var attempts = claimed.First(c => c.Id == id).Attempts;
            var retryAt = now + TimeSpan.FromSeconds(Math.Min(300, Math.Pow(2, Math.Min(attempts, 9))));
            var text = error.Length > 500 ? error[..500] : error;
            await db.OutboxMessages.Where(m => m.Id == id)
                .ExecuteUpdateAsync(s => s
                    .SetProperty(m => m.NextAttemptAt, retryAt)
                    .SetProperty(m => m.LastError, text), cancellationToken);
        }

        if (!failed.IsEmpty)
        {
            logger.LogWarning("АИС не приняла {Failed} из {Total} назначений, повторим позже", failed.Count, claimed.Count);
        }

        return claimed.Count;
    }

    internal sealed class ClaimedMessage
    {
        public long Id { get; set; }
        public long OrderId { get; set; }
        public long ExecutorId { get; set; }
        public int Attempts { get; set; }
    }
}
