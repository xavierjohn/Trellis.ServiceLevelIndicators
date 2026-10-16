namespace Trellis.ServiceLevelIndicators;

using System.Diagnostics.Metrics;

/// <summary>
/// Options for configuring the Service Level Indicator.
/// LocationId is required. CustomerResourceId defaults to <see cref="ServiceLevelIndicator.UnknownCustomerResourceId"/> when not configured.
/// Configuration is copied when the indicator is constructed; caller options are not modified.
/// </summary>
public class ServiceLevelIndicatorOptions
{
    private IList<TimeSpan> _latencyThresholds = new List<TimeSpan>();

    /// <summary>
    /// The meter that is used to create the histogram that reports the latency.
    /// Configure this during startup; the value is read once when <see cref="ServiceLevelIndicator"/> is constructed.
    /// An internally-created meter is exposed on the indicator's options copy, not written into caller configuration.
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
    public IList<TimeSpan> LatencyThresholds => _latencyThresholds;

    /// <summary>
    /// Obsolete. Activity status is no longer emitted as a metric dimension.
    /// </summary>
    [Obsolete("Activity status is no longer emitted as a metric dimension. Use SliOutcome instead.")]
    public string ActivityStatusCodeAttributeName { get; set; } = "activity.status.code";

    /// <summary>
    /// Automatically measure eligible ASP.NET Core endpoints.
    /// If false, endpoints must opt in with the ServiceLevelIndicator attribute or endpoint metadata.
    /// Explicit endpoint exclusion takes precedence in either mode.
    /// Manual core measurements are unaffected.
    /// </summary>
    public bool AutomaticallyEmitted { get; set; } = true;

    internal ServiceLevelIndicatorOptions Copy()
    {
        var copy = (ServiceLevelIndicatorOptions)MemberwiseClone();
        copy._latencyThresholds = new List<TimeSpan>(_latencyThresholds);
        return copy;
    }
}
