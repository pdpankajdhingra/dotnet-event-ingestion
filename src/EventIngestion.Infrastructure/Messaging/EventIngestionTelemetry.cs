using System.Diagnostics;
using System.Diagnostics.Metrics;

namespace EventIngestion.Infrastructure.Messaging;

public static class EventIngestionTelemetry
{
    public const string ActivitySourceName = "EventIngestion.Messaging";
    public const string MeterName = "EventIngestion.Messaging";

    public static ActivitySource ActivitySource { get; } = new(ActivitySourceName);

    public static Meter Meter { get; } = new(MeterName);

    internal static Counter<long> PublishedEvents { get; } =
        Meter.CreateCounter<long>("eventing.publish.successes", unit: "{event}");

    internal static Counter<long> FailedEvents { get; } =
        Meter.CreateCounter<long>("eventing.publish.failures", unit: "{event}");

    internal static Histogram<double> PublishDuration { get; } =
        Meter.CreateHistogram<double>("eventing.publish.duration", unit: "ms");
}
