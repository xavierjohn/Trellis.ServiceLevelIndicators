namespace Trellis.ServiceLevelIndicators;

using System.Collections.Generic;
using System.Diagnostics;
using System.Diagnostics.Metrics;
using System.Reflection;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

/// <summary>
/// Core service that creates and records Service Level Indicator metrics
/// using an OpenTelemetry <see cref="Histogram{T}"/>.
/// </summary>
/// <remarks>
/// Registered as a singleton. The container disposes this instance at host shutdown.
/// When no <see cref="Meter"/> is supplied via <see cref="ServiceLevelIndicatorOptions.Meter"/>,
/// this class creates one internally and disposes it on shutdown. A user-supplied
/// <see cref="Meter"/> is never disposed by this class; the caller owns its lifetime.
/// </remarks>
public sealed class ServiceLevelIndicator : IDisposable
{
    private const string ThresholdAttributeName = "sli.latency.threshold_ms";
    private const string ThresholdResultAttributeName = "sli.latency.result";
    private static readonly Action<ILogger, string, Exception?> _logRecordingFailed = LoggerMessage.Define<string>(
        LogLevel.Warning,
        new EventId(1, "MeasurementRecordingFailed"),
        "SLI measurement {Operation} could not be recorded.");

    /// <summary>
    /// Default meter name used when no <see cref="Meter"/> is provided in options.
    /// </summary>
    public const string DefaultMeterName = "Trellis.SLI";

    /// <summary>
    /// Default value used when no customer resource identifier is available.
    /// </summary>
    public const string UnknownCustomerResourceId = "Unknown";

    /// <summary>
    /// Gets this instance's options copy, including its effective meter.
    /// </summary>
    public ServiceLevelIndicatorOptions ServiceLevelIndicatorOptions { get; }

    private readonly Histogram<long> _responseLatencyHistogram;
    private readonly Counter<long> _unknownCustomerResourceIdCounter;
    private readonly Counter<long>? _durationThresholdCounter;
    private readonly long[] _latencyThresholds;
    private readonly Meter _meter;
    private readonly bool _ownsMeter;
    private readonly ILogger<ServiceLevelIndicator> _logger;
    private bool _disposed;

    public ServiceLevelIndicator(IOptions<ServiceLevelIndicatorOptions> options) :
        this(options, NullLogger<ServiceLevelIndicator>.Instance)
    { }

    /// <summary>
    /// Creates an instance with diagnostic logging for measurement recording failures.
    /// </summary>
    /// <param name="options">Startup configuration, copied for this instance without modifying the caller's options.</param>
    /// <param name="logger">Logger for recording failures; logging faults are reported through EventSource.</param>
    public ServiceLevelIndicator(
        IOptions<ServiceLevelIndicatorOptions> options,
        ILogger<ServiceLevelIndicator> logger)
    {
        ArgumentNullException.ThrowIfNull(logger);
        _logger = logger;
        ServiceLevelIndicatorOptions = options.Value.Copy();

        ArgumentException.ThrowIfNullOrWhiteSpace(ServiceLevelIndicatorOptions.LocationId, nameof(ServiceLevelIndicatorOptions.LocationId));
        ArgumentException.ThrowIfNullOrWhiteSpace(ServiceLevelIndicatorOptions.DurationInstrumentName, nameof(ServiceLevelIndicatorOptions.DurationInstrumentName));
        _latencyThresholds = GetLatencyThresholds(ServiceLevelIndicatorOptions.LatencyThresholds);

        if (ServiceLevelIndicatorOptions.Meter == null)
        {
            AssemblyName AssemblyName = typeof(ServiceLevelIndicator).Assembly.GetName();
            string InstrumentationVersion = AssemblyName.Version!.ToString();
            _meter = new Meter(DefaultMeterName, InstrumentationVersion);
            ServiceLevelIndicatorOptions.Meter = _meter;
            _ownsMeter = true;
        }
        else
        {
            _meter = ServiceLevelIndicatorOptions.Meter;
            _ownsMeter = false;
        }

        _responseLatencyHistogram = _meter.CreateHistogram<long>(ServiceLevelIndicatorOptions.DurationInstrumentName, "ms", "Duration of the operation.");
        _unknownCustomerResourceIdCounter = _meter.CreateCounter<long>("sli.diagnostics.unknown_customer_resource_id", description: "Count of SLI measurements emitted with an unknown customer resource identifier.");
        if (_latencyThresholds.Length > 0)
        {
            _durationThresholdCounter = _meter.CreateCounter<long>(
                "operation.duration.threshold.count",
                "{operation}",
                "Count of operations classified against configured duration thresholds.");
        }
    }

    /// <summary>
    /// Disposes the internally-created <see cref="Meter"/> if this instance created it.
    /// User-supplied meters are never disposed by this class.
    /// </summary>
    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        if (_ownsMeter)
            _meter.Dispose();
    }

    internal void ReportRecordingFailure(string operation, Exception exception)
    {
        ServiceLevelIndicatorEventSource.Log.ReportRecordingFailure(operation, exception);
        try
        {
            _logRecordingFailed(_logger, operation, exception);
        }
        catch (Exception loggingException)
        {
            ServiceLevelIndicatorEventSource.Log.ReportLoggingFailure(
                operation, "measurement recording", exception, loggingException);
        }
    }

    /// <summary>
    /// Records an operation measurement using the default <see cref="ServiceLevelIndicatorOptions.CustomerResourceId"/>.
    /// </summary>
    /// <param name="operation">The operation name.</param>
    /// <param name="elapsedTime">Elapsed time in milliseconds.</param>
    /// <param name="attributes">Additional measurement attributes.</param>
    public void Record(string operation, long elapsedTime, params KeyValuePair<string, object?>[] attributes) =>
        Record(operation, ServiceLevelIndicatorOptions.CustomerResourceId, elapsedTime, attributes);

    /// <summary>
    /// Records an operation measurement using the default <see cref="ServiceLevelIndicatorOptions.CustomerResourceId"/>.
    /// </summary>
    /// <param name="operation">The operation name.</param>
    /// <param name="elapsedTime">Elapsed time in milliseconds.</param>
    /// <param name="outcome">The SLI outcome.</param>
    /// <param name="attributes">Additional measurement attributes.</param>
    public void Record(string operation, long elapsedTime, SliOutcome outcome, params KeyValuePair<string, object?>[] attributes) =>
        Record(operation, ServiceLevelIndicatorOptions.CustomerResourceId, elapsedTime, outcome, attributes);

    /// <summary>
    /// Records an operation measurement with an explicit customer resource identifier.
    /// </summary>
    /// <param name="operation">The operation name.</param>
    /// <param name="customerResourceId">The customer resource identifier.</param>
    /// <param name="elapsedTime">Elapsed time in milliseconds.</param>
    /// <param name="attributes">Additional measurement attributes.</param>
    public void Record(string operation, string customerResourceId, long elapsedTime, params KeyValuePair<string, object?>[] attributes)
        => Record(operation, customerResourceId, elapsedTime, SliOutcome.Ignored, attributes);

    /// <summary>
    /// Records an operation measurement with an explicit customer resource identifier.
    /// </summary>
    /// <param name="operation">The operation name.</param>
    /// <param name="customerResourceId">The customer resource identifier.</param>
    /// <param name="elapsedTime">Elapsed time in milliseconds.</param>
    /// <param name="outcome">The SLI outcome.</param>
    /// <param name="attributes">Additional measurement attributes.</param>
    public void Record(string operation, string customerResourceId, long elapsedTime, SliOutcome outcome, params KeyValuePair<string, object?>[] attributes)
    {
        ValidateAttributes(attributes);
        ValidateDuplicateArgumentAttributeNames(attributes);
        RecordMeasurement(operation, customerResourceId, elapsedTime, outcome, attributes);
    }

    internal void RecordMeasurement(string operation, string customerResourceId, long elapsedTime, SliOutcome outcome, params KeyValuePair<string, object?>[] attributes)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(operation, nameof(operation));
        ValidateRecordAttributeNames(attributes);
        var wireOutcome = ToWireValue(outcome);

        customerResourceId = NormalizeCustomerResourceId(customerResourceId);
        RecordUnknownCustomerResourceId(operation, customerResourceId);
        Activity.Current?.SetStatus(GetActivityStatusCode(outcome));

        var tagList = new TagList
        {
            { "CustomerResourceId", customerResourceId },
            { "LocationId", ServiceLevelIndicatorOptions.LocationId },
            { "Operation", operation },
            { "Outcome", wireOutcome }
        };

        for (var i = 0; i < attributes.Length; i++)
            tagList.Add(attributes[i]);

        _responseLatencyHistogram.Record(elapsedTime, tagList);
        RecordThresholdCounts(elapsedTime, tagList);
    }

    private void RecordThresholdCounts(long elapsedTime, TagList durationTags)
    {
        if (_durationThresholdCounter is null || !_durationThresholdCounter.Enabled)
            return;

        var counterTags = new TagList();
        foreach (var tag in durationTags)
        {
            if (tag.Key is not ("http.response.status.code" or "http.request.method" or "sli.request.abort_exception"))
                counterTags.Add(tag);
        }

        foreach (var threshold in _latencyThresholds)
        {
            var thresholdTags = counterTags;
            thresholdTags.Add(ThresholdAttributeName, threshold);
            thresholdTags.Add(ThresholdResultAttributeName, elapsedTime <= threshold ? "within" : "exceeded");
            _durationThresholdCounter.Add(1, thresholdTags);
        }
    }

    private static long[] GetLatencyThresholds(IList<TimeSpan> latencyThresholds)
    {
        var thresholds = new long[latencyThresholds.Count];
        var distinctThresholds = new HashSet<long>();
        for (var i = 0; i < thresholds.Length; i++)
        {
            var ticks = latencyThresholds[i].Ticks;
            if (ticks <= 0 || ticks % TimeSpan.TicksPerMillisecond != 0)
            {
                throw new ArgumentException(
                    "Latency thresholds must be positive whole-millisecond durations.",
                    nameof(latencyThresholds));
            }

            var milliseconds = ticks / TimeSpan.TicksPerMillisecond;
            if (!distinctThresholds.Add(milliseconds))
            {
                throw new ArgumentException(
                    "Latency thresholds must be distinct.",
                    nameof(latencyThresholds));
            }

            thresholds[i] = milliseconds;
        }

        return thresholds;
    }

    /// <summary>
    /// Starts measuring an operation. Dispose the returned <see cref="MeasuredOperation"/> to record the elapsed time.
    /// </summary>
    /// <param name="operation">The operation name.</param>
    /// <param name="attributes">Additional measurement attributes.</param>
    /// <returns>A <see cref="MeasuredOperation"/> that records the metric on disposal.</returns>
    public MeasuredOperation StartMeasuring(string operation, params KeyValuePair<string, object?>[] attributes) => new(this, operation, attributes);

    /// <summary>
    /// Measures a synchronous operation and infers the SLI outcome from completion or exception.
    /// </summary>
    public void Measure(string operation, Action action, params KeyValuePair<string, object?>[] attributes)
    {
        ArgumentNullException.ThrowIfNull(action);

        using var measuredOperation = StartMeasuring(operation, attributes);
        try
        {
            action();
            measuredOperation.SetInferredOutcome(SliOutcome.Success);
        }
        catch (OperationCanceledException)
        {
            measuredOperation.ForceOutcome(SliOutcome.Ignored);
            throw;
        }
        catch
        {
            measuredOperation.ForceOutcome(SliOutcome.Failure);
            throw;
        }
    }

    /// <summary>
    /// Measures a synchronous operation and returns its result.
    /// </summary>
    public T Measure<T>(string operation, Func<T> action, params KeyValuePair<string, object?>[] attributes) =>
        Measure(operation, action, static _ => SliOutcome.Success, attributes);

    /// <summary>
    /// Measures a synchronous operation, classifies its returned value, and returns that same value.
    /// </summary>
    /// <param name="operation">The bounded operation name.</param>
    /// <param name="action">The operation to measure.</param>
    /// <param name="classifyOutcome">Classifies the result after the action completes successfully.</param>
    /// <param name="attributes">Additional measurement attributes.</param>
    /// <returns>The original result returned by <paramref name="action"/>.</returns>
    /// <remarks>
    /// Action and classifier exceptions propagate and record Failure. OperationCanceledException records Ignored.
    /// The classifier must return a defined SliOutcome.
    /// Classifier execution is included in the measured duration; keep classification cheap and synchronous.
    /// </remarks>
    public T Measure<T>(
        string operation,
        Func<T> action,
        Func<T, SliOutcome> classifyOutcome,
        params KeyValuePair<string, object?>[] attributes)
    {
        ArgumentNullException.ThrowIfNull(action);
        ArgumentNullException.ThrowIfNull(classifyOutcome);

        using var measuredOperation = StartMeasuring(operation, attributes);
        try
        {
            var result = action();
            var outcome = classifyOutcome(result);
            // Validate before assignment so invalid selector results record Failure.
            _ = ToWireValue(outcome);
            measuredOperation.SetInferredOutcome(outcome);
            return result;
        }
        catch (OperationCanceledException)
        {
            measuredOperation.ForceOutcome(SliOutcome.Ignored);
            throw;
        }
        catch
        {
            measuredOperation.ForceOutcome(SliOutcome.Failure);
            throw;
        }
    }

    /// <summary>
    /// Measures an asynchronous operation and infers the SLI outcome from completion or exception.
    /// </summary>
    public async Task MeasureAsync(string operation, Func<Task> action, params KeyValuePair<string, object?>[] attributes)
    {
        ArgumentNullException.ThrowIfNull(action);

        using var measuredOperation = StartMeasuring(operation, attributes);
        try
        {
            await action().ConfigureAwait(false);
            measuredOperation.SetInferredOutcome(SliOutcome.Success);
        }
        catch (OperationCanceledException)
        {
            measuredOperation.ForceOutcome(SliOutcome.Ignored);
            throw;
        }
        catch
        {
            measuredOperation.ForceOutcome(SliOutcome.Failure);
            throw;
        }
    }

    /// <summary>
    /// Measures an asynchronous operation and returns its result.
    /// </summary>
    public Task<T> MeasureAsync<T>(string operation, Func<Task<T>> action, params KeyValuePair<string, object?>[] attributes) =>
        MeasureAsync(operation, action, static _ => SliOutcome.Success, attributes);

    /// <summary>
    /// Measures an asynchronous operation, classifies its returned value, and returns that same value.
    /// </summary>
    /// <param name="operation">The bounded operation name.</param>
    /// <param name="action">The asynchronous operation to measure.</param>
    /// <param name="classifyOutcome">Classifies the result after the action completes successfully.</param>
    /// <param name="attributes">Additional measurement attributes.</param>
    /// <returns>The original result returned by <paramref name="action"/>.</returns>
    /// <remarks>
    /// Action and classifier exceptions propagate and record Failure. OperationCanceledException records Ignored.
    /// The synchronous classifier must return a defined SliOutcome.
    /// Classifier execution is included in the measured duration; keep classification cheap.
    /// </remarks>
    public async Task<T> MeasureAsync<T>(
        string operation,
        Func<Task<T>> action,
        Func<T, SliOutcome> classifyOutcome,
        params KeyValuePair<string, object?>[] attributes)
    {
        ArgumentNullException.ThrowIfNull(action);
        ArgumentNullException.ThrowIfNull(classifyOutcome);

        using var measuredOperation = StartMeasuring(operation, attributes);
        try
        {
            var result = await action().ConfigureAwait(false);
            var outcome = classifyOutcome(result);
            // Validate before assignment so invalid selector results record Failure.
            _ = ToWireValue(outcome);
            measuredOperation.SetInferredOutcome(outcome);
            return result;
        }
        catch (OperationCanceledException)
        {
            measuredOperation.ForceOutcome(SliOutcome.Ignored);
            throw;
        }
        catch
        {
            measuredOperation.ForceOutcome(SliOutcome.Failure);
            throw;
        }
    }

    internal static void ValidateAttributeName(string attribute)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(attribute, nameof(attribute));

        if (attribute is "CustomerResourceId"
            or "LocationId"
            or "Operation"
            or "Outcome"
            or "activity.status.code"
            or "http.request.method"
            or "http.response.status.code"
            or "sli.request.aborted"
            or "sli.request.abort_exception"
            or ThresholdAttributeName
            or ThresholdResultAttributeName)
        {
            throw new ArgumentException(
                $"'{attribute}' is a reserved Service Level Indicator attribute name and cannot be used as a custom metric attribute.",
                nameof(attribute));
        }
    }

    private static void ValidateAttributes(ReadOnlySpan<KeyValuePair<string, object?>> attributes)
    {
        for (var i = 0; i < attributes.Length; i++)
            ValidateAttributeName(attributes[i].Key);
    }

    private static void ValidateDuplicateArgumentAttributeNames(ReadOnlySpan<KeyValuePair<string, object?>> attributes)
    {
        HashSet<string>? names = null;

        for (var i = 0; i < attributes.Length; i++)
        {
            names ??= new HashSet<string>(attributes.Length, StringComparer.Ordinal);

            if (!names.Add(attributes[i].Key))
            {
                throw new ArgumentException(
                    $"Service Level Indicator attribute '{attributes[i].Key}' was added more than once. Metric attribute names must be unique.",
                    nameof(attributes));
            }
        }
    }

    private static void ValidateRecordAttributeNames(ReadOnlySpan<KeyValuePair<string, object?>> attributes)
    {
        HashSet<string>? names = null;

        for (var i = 0; i < attributes.Length; i++)
        {
            if (string.IsNullOrWhiteSpace(attributes[i].Key))
            {
                throw new ArgumentException(
                    "Service Level Indicator attribute names cannot be null, empty, or whitespace.",
                    nameof(attributes));
            }

            names ??= new HashSet<string>(attributes.Length + 6, StringComparer.Ordinal)
            {
                "CustomerResourceId",
                "LocationId",
                "Operation",
                "Outcome",
                ThresholdAttributeName,
                ThresholdResultAttributeName
            };

            if (!names.Add(attributes[i].Key))
            {
                throw new InvalidOperationException(
                    $"Service Level Indicator attribute '{attributes[i].Key}' was added more than once. Metric attribute names must be unique.");
            }
        }
    }

    /// <summary>
    /// Creates a customer resource identifier from a Service Tree GUID.
    /// </summary>
    /// <param name="serviceId">A non-empty Service Tree identifier.</param>
    /// <returns>A formatted <c>ServiceTreeId://</c> URI string.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="serviceId"/> is <see cref="Guid.Empty"/>.</exception>
    public static string CreateCustomerResourceId(Guid serviceId)
    {
        if (serviceId == Guid.Empty) throw new ArgumentNullException(nameof(serviceId));
        return "ServiceTreeId://" + serviceId.ToString();
    }

    /// <summary>
    /// Creates a location identifier in the <c>ms-loc://az</c> format.
    /// </summary>
    /// <param name="cloud">Cloud name (e.g., "public").</param>
    /// <param name="region">Optional region (e.g., "West US 3").</param>
    /// <param name="zone">Optional availability zone.</param>
    /// <returns>A formatted location identifier.</returns>
    public static string CreateLocationId(string cloud, string? region = null, string? zone = null)
    {
        var arr = new string?[] { "ms-loc://az", cloud, region, zone };
        var id = string.Join("/", arr.Where(s => !string.IsNullOrEmpty(s)));
        return id;
    }

    internal static string ToWireValue(SliOutcome outcome) => outcome switch
    {
        SliOutcome.Success => "Success",
        SliOutcome.Failure => "Failure",
        SliOutcome.ClientError => "ClientError",
        SliOutcome.Ignored => "Ignored",
        _ => throw new ArgumentOutOfRangeException(nameof(outcome), outcome, "Unknown SLI outcome.")
    };

    private static ActivityStatusCode GetActivityStatusCode(SliOutcome outcome) => outcome switch
    {
        SliOutcome.Success => ActivityStatusCode.Ok,
        SliOutcome.Failure => ActivityStatusCode.Error,
        SliOutcome.ClientError or SliOutcome.Ignored => ActivityStatusCode.Unset,
        _ => ActivityStatusCode.Unset
    };

    private static string NormalizeCustomerResourceId(string customerResourceId) =>
        string.IsNullOrWhiteSpace(customerResourceId) ? UnknownCustomerResourceId : customerResourceId;

    private void RecordUnknownCustomerResourceId(string operation, string customerResourceId)
    {
        if (!string.Equals(customerResourceId, UnknownCustomerResourceId, StringComparison.Ordinal))
            return;

        _unknownCustomerResourceIdCounter.Add(
            1,
            new KeyValuePair<string, object?>("Operation", operation),
            new KeyValuePair<string, object?>("LocationId", ServiceLevelIndicatorOptions.LocationId));
    }
}
