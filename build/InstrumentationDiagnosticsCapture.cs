namespace Trellis.ServiceLevelIndicators.Testing;

using System.Collections.Concurrent;
using System.Diagnostics.Tracing;
using Microsoft.Extensions.Logging;

internal sealed class InstrumentationDiagnosticsCapture(string operation) : EventListener
{
    public ConcurrentQueue<CapturedInstrumentationFailure> Failures { get; } = [];

    protected override void OnEventSourceCreated(EventSource eventSource)
    {
        if (eventSource.Name == "Trellis-ServiceLevelIndicators")
            EnableEvents(eventSource, EventLevel.Verbose);
    }

    protected override void OnEventWritten(EventWrittenEventArgs eventData)
    {
        if (eventData.Payload is null || eventData.PayloadNames is null ||
            eventData.Payload.Count == 0 || !Equals(eventData.Payload[0], operation))
            return;

        Failures.Enqueue(new CapturedInstrumentationFailure(
            eventData.EventName ?? string.Empty,
            eventData.PayloadNames.Select((name, index) => new KeyValuePair<string, object?>(name, eventData.Payload[index]))
                .ToDictionary(pair => pair.Key, pair => pair.Value, StringComparer.Ordinal)));
    }
}

internal sealed record CapturedInstrumentationFailure(string Name, IReadOnlyDictionary<string, object?> Data);

internal sealed class DiagnosticTestLogger<T>(string? throwFrom = null, bool enabled = true) : ILogger<T>
{
    public ConcurrentQueue<CapturedLog> Entries { get; } = [];

    public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

    public bool IsEnabled(LogLevel logLevel)
    {
        if (throwFrom == nameof(IsEnabled))
            throw new InvalidOperationException("Broken logger: IsEnabled");
        return enabled;
    }

    public void Log<TState>(
        LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
    {
        if (throwFrom == nameof(Log))
            throw new InvalidOperationException("Broken logger: Log");
        Entries.Enqueue(new CapturedLog(logLevel, exception, formatter(state, exception)));
    }
}

internal sealed record CapturedLog(LogLevel Level, Exception? Exception, string Message);
