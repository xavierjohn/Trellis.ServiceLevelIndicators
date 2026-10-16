namespace Trellis.ServiceLevelIndicators;

using System.Diagnostics.Metrics;

/// <summary>
/// Options for configuring the Service Level Indicator.
/// LocationId is required. CustomerResourceId defaults to <see cref="ServiceLevelIndicator.UnknownCustomerResourceId"/> when not configured.
/// </summary>
public class ServiceLevelIndicatorOptions
{
    /// <summary>
    /// The meter that is used to create the histogram that reports the latency.
    /// Configure this during startup; the value is read once when <see cref="ServiceLevelIndicator"/> is constructed.
    /// </summary>
    public Meter Meter { get; set; } = null!;

    /// <summary>
    /// CustomerResourceId is the unique identifier for the customer like subscriptionId, tenantId, etc.
    /// CustomerResourceId can be set for the entire service here or in each API method.
    /// </summary>
    public string CustomerResourceId { get; set; } = ServiceLevelIndicator.UnknownCustomerResourceId;

    /// <summary>
    /// Location where the service is running.
    /// </summary>
    public string LocationId { get; set; } = string.Empty;

    /// <summary>
    /// The instrument name created on the given meter. Cannot be null.
    /// </summary>
    public string DurationInstrumentName { get; set; } = "operation.duration";

    /// <summary>
    /// Opt-in duration thresholds for <c>operation.duration.threshold.count</c>.
    /// Configure positive, distinct, whole-millisecond values during startup.
    /// Values are snapshotted when <see cref="ServiceLevelIndicator"/> is constructed.
    /// An observation is within a threshold when its recorded milliseconds are less than or equal to it.
    /// </summary>
    public IList<TimeSpan> LatencyThresholds { get; } = new List<TimeSpan>();

    /// <summary>
    /// Obsolete. Activity status is no longer emitted as a metric dimension.
    /// </summary>
    [Obsolete("Activity status is no longer emitted as a metric dimension. Use SliOutcome instead.")]
    public string ActivityStatusCodeAttributeName { get; set; } = "activity.status.code";

    /// <summary>
    /// Automatically emit for all API methods.
    /// If false, use the ServiceLevelIndicator Attribute to emit.
    /// </summary>
    public bool AutomaticallyEmitted { get; set; } = true;
}
