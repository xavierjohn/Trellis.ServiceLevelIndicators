namespace Trellis.ServiceLevelIndicators;

using System.Diagnostics.Tracing;

/// <summary>
/// Shared diagnostic contract for SLI recording, classification, enrichment, and logging failures.
/// </summary>
/// <remarks>
/// Exception text is limited to 2048 UTF-16 code units but is not redacted and may contain request data.
/// </remarks>
[EventSource(Name = "Trellis-ServiceLevelIndicators")]
public sealed class ServiceLevelIndicatorEventSource : EventSource
{
    private const int MaxExceptionTextLength = 2048;

    /// <summary>
    /// Gets the shared <c>Trellis-ServiceLevelIndicators</c> diagnostic provider.
    /// </summary>
    public static ServiceLevelIndicatorEventSource Log { get; } = new();

    private ServiceLevelIndicatorEventSource()
    {
    }

    /// <summary>
    /// Reports a scope recording failure without retrying or changing the operation outcome.
    /// </summary>
    [NonEvent]
    public void ReportRecordingFailure(string operation, Exception exception)
    {
        ArgumentNullException.ThrowIfNull(exception);
        MeasurementRecordingFailed(operation, Describe(exception));
    }

    /// <summary>
    /// Reports a failed diagnostic logger, optionally including the original instrumentation failure.
    /// </summary>
    [NonEvent]
    public void ReportLoggingFailure(
        string operation, string stage, Exception? originalException, Exception loggingException)
    {
        ArgumentNullException.ThrowIfNull(loggingException);
        DiagnosticLoggingFailed(operation, stage, Describe(originalException), Describe(loggingException));
    }

    /// <summary>
    /// Reports an HTTP classifier failure and the built-in outcome used instead.
    /// </summary>
    [NonEvent]
    public void ReportClassifierFailure(string operation, SliOutcome defaultOutcome, Exception exception)
    {
        ArgumentNullException.ThrowIfNull(exception);
        HttpOutcomeClassificationFailed(operation, ServiceLevelIndicator.ToWireValue(defaultOutcome), Describe(exception));
    }

    /// <summary>
    /// Reports a failed enrichment independently of diagnostic logging configuration.
    /// </summary>
    [NonEvent]
    public void ReportEnrichmentFailure(string operation, string enrichmentType, Exception exception)
    {
        ArgumentNullException.ThrowIfNull(exception);
        EnrichmentFailed(operation, enrichmentType, Describe(exception));
    }

    /// <summary>
    /// Writes recording-failure event 1 with bounded exception text.
    /// </summary>
    [Event(1, Level = EventLevel.Warning, Message = "SLI measurement '{0}' could not be recorded. {1}")]
    public void MeasurementRecordingFailed(string operation, string error) =>
        WriteEvent(1, operation, Truncate(error));

    /// <summary>
    /// Writes logging-failure event 2 with bounded original and logging exception text.
    /// </summary>
    [Event(2, Level = EventLevel.Warning, Message = "SLI logging failed for '{0}' during {1}. Original failure: {2}. Logging failure: {3}")]
    public void DiagnosticLoggingFailed(string operation, string stage, string originalError, string loggingError) =>
        WriteEvent(2, operation, stage, Truncate(originalError), Truncate(loggingError));

    /// <summary>
    /// Writes classifier-fallback event 3, including the built-in outcome.
    /// </summary>
    [Event(3, Level = EventLevel.Warning, Message = "SLI HTTP classifier for '{0}' failed; using '{1}'. {2}")]
    public void HttpOutcomeClassificationFailed(string operation, string defaultOutcome, string error) =>
        WriteEvent(3, operation, defaultOutcome, Truncate(error));

    /// <summary>
    /// Writes enrichment-failure event 4, including the enrichment type.
    /// </summary>
    [Event(4, Level = EventLevel.Warning, Message = "SLI enrichment '{1}' failed for '{0}'. {2}")]
    public void EnrichmentFailed(string operation, string enrichmentType, string error) =>
        WriteEvent(4, operation, enrichmentType, Truncate(error));

    [NonEvent]
    private static string Truncate(string? text)
    {
        if (string.IsNullOrEmpty(text))
            return string.Empty;
        if (text.Length <= MaxExceptionTextLength)
            return text;

        var length = MaxExceptionTextLength - 3;
        if (char.IsHighSurrogate(text[length - 1]))
            length--;
        return string.Concat(text.AsSpan(0, length), "...");
    }

    [NonEvent]
    private static string Describe(Exception? exception)
    {
        if (exception is null)
            return string.Empty;

        try
        {
            return exception.ToString();
        }
        catch (Exception)
        {
            return exception.GetType().FullName ?? exception.GetType().Name;
        }
    }
}
