using EventIngestion.Application.Abstractions;
using EventIngestion.Application.Events;
using EventIngestion.Domain.Events;
using EventIngestion.Infrastructure.Outbox;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Npgsql;

namespace EventIngestion.Api.Outbox;

public static class EventAcceptanceExtensions
{
    public static IServiceCollection AddEventAcceptance(this IServiceCollection services, IConfiguration configuration)
    {
        if (!configuration.GetValue<bool>("Outbox:Enabled"))
        {
            services.AddScoped<IEventAcceptance, DirectEventAcceptance>();
            return services;
        }

        var connectionString = configuration.GetConnectionString("Outbox");
        if (string.IsNullOrWhiteSpace(connectionString))
        {
            throw new InvalidOperationException("ConnectionStrings:Outbox is required when the outbox is enabled.");
        }
        services.AddSingleton(NpgsqlDataSource.Create(connectionString));
        services.AddSingleton<PostgresOutbox>();
        services.AddSingleton<IEventAcceptance>(provider => provider.GetRequiredService<PostgresOutbox>());
        services.AddHostedService<OutboxPublisherService>();
        services.AddHealthChecks().AddCheck<OutboxHealthCheck>("outbox", tags: ["ready"], timeout: TimeSpan.FromSeconds(5));
        return services;
    }
}

public sealed class DirectEventAcceptance(IngestEventHandler handler) : IEventAcceptance
{
    public bool RequiresMessageId => false;

    public async Task<AcceptanceReceipt?> AcceptAsync(EventEnvelope message, DateTimeOffset? requestedOccurredAtUtc, CancellationToken cancellationToken)
    {
        await handler.HandleAsync(message, cancellationToken);
        return new AcceptanceReceipt(message.MessageId!, message.CorrelationId!, "published");
    }
}

public sealed class OutboxHealthCheck(PostgresOutbox outbox) : IHealthCheck
{
    public async Task<HealthCheckResult> CheckHealthAsync(HealthCheckContext context, CancellationToken cancellationToken = default)
    {
        await outbox.CheckHealthAsync(cancellationToken);
        return HealthCheckResult.Healthy();
    }
}
