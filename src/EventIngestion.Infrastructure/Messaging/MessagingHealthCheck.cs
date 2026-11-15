using Microsoft.Extensions.Diagnostics.HealthChecks;

namespace EventIngestion.Infrastructure.Messaging;

public sealed class MessagingHealthCheck(IBrokerHealthProbe brokerHealthProbe) : IHealthCheck
{
    public async Task<HealthCheckResult> CheckHealthAsync(
        HealthCheckContext context,
        CancellationToken cancellationToken = default)
    {
        try
        {
            await brokerHealthProbe.CheckHealthAsync(cancellationToken);
            return HealthCheckResult.Healthy($"{brokerHealthProbe.ProviderName} is reachable.");
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception)
        {
            return HealthCheckResult.Unhealthy(
                $"{brokerHealthProbe.ProviderName} health probe failed.",
                exception);
        }
    }
}
