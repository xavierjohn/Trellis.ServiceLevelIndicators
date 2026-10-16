namespace Trellis.ServiceLevelIndicators;

using System;
using System.Collections.Generic;
using System.Diagnostics;

/// <summary>
/// Represents an in-flight measured operation. Disposing this instance stops the stopwatch
/// and attempts to record the elapsed time once. Recording failures are reported through
/// diagnostic logging and EventSource, not thrown from disposal.
/// </summary>
public class MeasuredOperation : IDisposable
{
    private bool _disposed;
    private readonly ServiceLevelIndicator _serviceLevelIndicator;
    private readonly Stopwatch _stopWatch;
    private readonly HashSet<string> _attributeNames;
    private SliOutcome _outcome = SliOutcome.Ignored;
    private bool _outcomeExplicitlySet;
    private readonly object _disposeLock = new();

    public MeasuredOperation(ServiceLevelIndicator serviceLevelIndicator, string operation, params KeyValuePair<string, object?>[] attributes) :
        this(serviceLevelIndicator, operation, serviceLevelIndicator.ServiceLevelIndicatorOptions.CustomerResourceId, attributes)
    { }

    public MeasuredOperation(ServiceLevelIndicator serviceLevelIndicator, string operation, string customerResourceId, params KeyValuePair<string, object?>[] attributes)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(operation);

        _serviceLevelIndicator = serviceLevelIndicator;
        Operation = operation;
        CustomerResourceId = customerResourceId;
        Attributes = new List<KeyValuePair<string, object?>>(attributes.Length);
        _attributeNames = new HashSet<string>(attributes.Length, StringComparer.Ordinal);
        for (var i = 0; i < attributes.Length; i++)
        {
            ServiceLevelIndicator.ValidateAttributeName(attributes[i].Key);
            ValidateUniqueAttributeName(attributes[i].Key);
            Attributes.Add(attributes[i]);
        }

        _stopWatch = Stopwatch.StartNew();
    }

    /// <summary>
    /// Gets or sets the operation name emitted as a tag.
    /// </summary>
    public string Operation { get; set; }

    /// <summary>
    /// Gets or sets the customer resource identifier emitted as a tag.
    /// </summary>
    public string CustomerResourceId { get; set; }

    /// <summary>
    /// Gets the additional OpenTelemetry attributes emitted with the measurement.
    /// </summary>
    public List<KeyValuePair<string, object?>> Attributes { get; }

    /// <summary>
    /// Sets the outcome recorded with the measurement.
    /// </summary>
    /// <param name="outcome">The SLI outcome.</param>
    /// <exception cref="ArgumentOutOfRangeException">The outcome is not a defined <see cref="SliOutcome"/>.</exception>
    public void SetOutcome(SliOutcome outcome)
    {
        _ = ServiceLevelIndicator.ToWireValue(outcome);
        _outcome = outcome;
        _outcomeExplicitlySet = true;
    }

    internal void SetInferredOutcome(SliOutcome outcome)
    {
        if (!_outcomeExplicitlySet)
            _outcome = outcome;
    }

    internal void ForceOutcome(SliOutcome outcome) => _outcome = outcome;

    /// <summary>
    /// Sets the outcome based on an <see cref="ActivityStatusCode"/>.
    /// </summary>
    /// <param name="activityStatusCode">The activity status code.</param>
    [Obsolete("Use SetOutcome(SliOutcome) instead.")]
    public void SetActivityStatusCode(ActivityStatusCode activityStatusCode) =>
        SetOutcome(activityStatusCode switch
        {
            ActivityStatusCode.Ok => SliOutcome.Success,
            ActivityStatusCode.Error => SliOutcome.Failure,
            _ => SliOutcome.Ignored
        });

    /// <summary>
    /// Adds a custom attribute to the measurement.
    /// </summary>
    /// <param name="attribute">The attribute name.</param>
    /// <param name="value">The attribute value.</param>
    public void AddAttribute(string attribute, object? value)
    {
        ServiceLevelIndicator.ValidateAttributeName(attribute);
        ValidateUniqueAttributeName(attribute);
        Attributes.Add(new KeyValuePair<string, object?>(attribute, value));
    }

    private void ValidateUniqueAttributeName(string attribute)
    {
        if (!_attributeNames.Add(attribute))
        {
            throw new ArgumentException(
                $"Service Level Indicator attribute '{attribute}' was added more than once. Metric attribute names must be unique.",
                nameof(attribute));
        }
    }

    protected virtual void Dispose(bool disposing)
    {
        lock (_disposeLock)
        {
            if (!_disposed)
            {
                _disposed = true;
                if (disposing)
                {
                    _stopWatch.Stop();
                    try
                    {
                        var elapsedTime = _stopWatch.ElapsedMilliseconds;
                        _serviceLevelIndicator.RecordMeasurement(Operation, CustomerResourceId, elapsedTime, _outcome, Attributes.ToArray());
                    }
                    catch (Exception ex)
                    {
                        _serviceLevelIndicator.ReportRecordingFailure(Operation, ex);
                    }
                }
            }
        }
    }

    /// <inheritdoc />
    public void Dispose()
    {
        Dispose(disposing: true);
        GC.SuppressFinalize(this);
    }
}
