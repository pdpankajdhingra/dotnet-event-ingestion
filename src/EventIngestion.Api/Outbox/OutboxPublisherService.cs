using EventIngestion.Application.Abstractions;
using EventIngestion.Infrastructure.Outbox;

namespace EventIngestion.Api.Outbox;

public sealed class OutboxPublisherService(PostgresOutbox outbox, IEventPublisher publisher, ILogger<OutboxPublisherService> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                if (!await outbox.PublishNextAsync(publisher, stoppingToken))
                {
                    await Task.Delay(TimeSpan.FromSeconds(2), stoppingToken);
                }
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception exception)
            {
                logger.LogError(exception, "Outbox dispatch failed; retrying after a delay.");
                await Task.Delay(TimeSpan.FromSeconds(5), stoppingToken);
            }
        }
    }
}
