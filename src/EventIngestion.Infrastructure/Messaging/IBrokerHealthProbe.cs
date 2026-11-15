namespace EventIngestion.Infrastructure.Messaging;

public interface IBrokerHealthProbe
{
    string ProviderName { get; }

    Task CheckHealthAsync(CancellationToken cancellationToken);
}
