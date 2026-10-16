namespace Trellis.ServiceLevelIndicators;

using System.Diagnostics.Tracing;

[EventSource(Name = "Trellis-ServiceLevelIndicators")]
internal sealed class ServiceLevelIndicatorEventSource : EventSource
{
    internal static readonly ServiceLevelIndicatorEventSource Log = new();

    private ServiceLevelIndicatorEventSource()
    {
    }

    [NonEvent]
    internal void ReportRecordingFailure(string operation, Exception exception) =>
        MeasurementRecordingFailed(operation, Describe(exception));

    [NonEvent]
    internal void ReportLoggingFailure(
        string operation, string stage, Exception? originalException, Exception loggingException) =>
        DiagnosticLoggingFailed(operation, stage, Describe(originalException), Describe(loggingException));

    [Event(1, Level = EventLevel.Warning, Message = "SLI measurement '{0}' could not be recorded. {1}")]
    public void MeasurementRecordingFailed(string operation, string error) =>
        WriteEvent(1, operation, error);

    [Event(2, Level = EventLevel.Warning, Message = "SLI logging failed for '{0}' during {1}. Original failure: {2}. Logging failure: {3}")]
    public void DiagnosticLoggingFailed(string operation, string stage, string originalError, string loggingError) =>
        WriteEvent(2, operation, stage, originalError, loggingError);

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
