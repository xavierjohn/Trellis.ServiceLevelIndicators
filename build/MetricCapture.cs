namespace Trellis.ServiceLevelIndicators.Testing;

using System.Collections.Concurrent;
using System.Diagnostics.Metrics;

internal sealed class MetricCapture : IDisposable
{
    private readonly MeterListener _listener;

    public Meter Meter { get; } = new($"SLI.Tests.{Guid.NewGuid():N}");
    public ConcurrentQueue<RecordedMeasurement> Measurements { get; } = [];
    public ConcurrentQueue<Instrument> Instruments { get; } = [];

    public MetricCapture()
    {
        _listener = new MeterListener
        {
            InstrumentPublished = (instrument, listener) =>
            {
                if (ReferenceEquals(instrument.Meter, Meter))
                {
                    Instruments.Enqueue(instrument);
                    listener.EnableMeasurementEvents(instrument);
                }
            }
        };
        _listener.SetMeasurementEventCallback<long>((instrument, value, tags, _) =>
            Measurements.Enqueue(new RecordedMeasurement(
                instrument,
                value,
                tags.ToArray().ToDictionary(tag => tag.Key, tag => tag.Value, StringComparer.Ordinal))));
        _listener.Start();
    }

    public RecordedMeasurement[] For(string instrumentName) =>
        Measurements.Where(measurement => measurement.Instrument.Name == instrumentName).ToArray();

    public void Dispose()
    {
        _listener.Dispose();
        Meter.Dispose();
    }
}

internal sealed record RecordedMeasurement(
    Instrument Instrument,
    long Value,
    IReadOnlyDictionary<string, object?> Tags);
