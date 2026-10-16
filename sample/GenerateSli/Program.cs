using System.Collections.Concurrent;
using System.Globalization;
using System.Runtime.ExceptionServices;
using System.Runtime.InteropServices;

GeneratorOptions options;
try
{
    options = GeneratorOptions.Parse(args);
}
catch (ArgumentException ex)
{
    Console.Error.WriteLine($"Invalid arguments: {ex.Message}");
    Console.Error.WriteLine(
        "Usage: GenerateSli [--base-url URL] [--phase-seconds N] [--requests-per-second N] " +
        "[--continuous] [--wait-for-ready]");
    return 2;
}

using var cancellation = new CancellationTokenSource();
var shutdown = new GeneratorShutdown(cancellation);
ConsoleCancelEventHandler cancelHandler = (_, eventArgs) => eventArgs.Cancel = shutdown.RequestCancellation();
Console.CancelKeyPress += cancelHandler;
using var termination = PosixSignalRegistration.Create(
    PosixSignal.SIGTERM, context => context.Cancel = shutdown.RequestCancellation());

using var client = new HttpClient
{
    BaseAddress = options.BaseAddress,
    Timeout = TimeSpan.FromSeconds(10),
};

Console.WriteLine($"SLI scenario -> {client.BaseAddress}");
Console.WriteLine($"Target: {options.RequestsPerSecond} requests/second, {options.PhaseSeconds} seconds/phase");
Console.WriteLine(options.Continuous ? "Continuous mode: repeat until stopped." : "Single-cycle mode.");

try
{
    return await ScenarioRunner.RunConsoleAsync(client, options, Console.Out, Console.Error, cancellation.Token);
}
finally
{
    Console.CancelKeyPress -= cancelHandler;
}

internal sealed class GeneratorShutdown(CancellationTokenSource cancellation)
{
    private int requested;

    public bool RequestCancellation()
    {
        if (Interlocked.Exchange(ref requested, 1) != 0)
            return false;

        cancellation.Cancel();
        return true;
    }
}

internal static class ScenarioRunner
{
    private const int MaximumInFlightRequests = 32;

    private static readonly Phase[] Phases =
    [
        new("Healthy baseline", ScenarioPhase.Healthy),
        new("Targeted project-red incident", ScenarioPhase.Incident),
        new("Client-error burst", ScenarioPhase.ClientError),
        new("Capacity pressure", ScenarioPhase.Throttled),
        new("Recovery", ScenarioPhase.Recovery),
    ];

    public static async Task<int> RunConsoleAsync(
        HttpClient client,
        GeneratorOptions options,
        TextWriter output,
        TextWriter error,
        CancellationToken cancellationToken,
        TimeProvider? timeProvider = null)
    {
        var run = RunAsync(client, options, output, error, cancellationToken, timeProvider);
        await run.ConfigureAwait(
            ConfigureAwaitOptions.ContinueOnCapturedContext | ConfigureAwaitOptions.SuppressThrowing);

        if (cancellationToken.IsCancellationRequested)
        {
            output.WriteLine("Scenario stopped.");
            return 0;
        }

        if (run.IsCanceled)
        {
            error.WriteLine($"Scenario stopped: an HTTP request to {client.BaseAddress} timed out.");
            return 1;
        }

        if (run.Exception is { } fault)
        {
            var failure = fault.InnerExceptions[0];
            error.WriteLine(failure is HttpRequestException
                ? $"Scenario stopped: an HTTP request to {client.BaseAddress} failed. " +
                  $"Start SliProductionDemo, check --base-url, and try again. {failure.Message}"
                : $"Scenario stopped: {failure.GetType().Name}: {failure.Message}");
            return 1;
        }

        return 0;
    }

    public static async Task RunAsync(
        HttpClient client,
        GeneratorOptions options,
        TextWriter output,
        TextWriter error,
        CancellationToken cancellationToken,
        TimeProvider? timeProvider = null)
    {
        timeProvider ??= TimeProvider.System;
        error = TextWriter.Synchronized(error);
        await EnsureReadyAsync(client, options.WaitForReady, error, timeProvider, cancellationToken);

        long sequence = 0;
        long cycle = 0;
        do
        {
            cancellationToken.ThrowIfCancellationRequested();
            output.WriteLine($"Cycle {++cycle}");
            foreach (var phase in Phases)
            {
                await RunPhaseAsync(
                    client, phase, options, () => Interlocked.Increment(ref sequence),
                    output, error, timeProvider, cancellationToken);
            }
        }
        while (options.Continuous);

        output.WriteLine("Scenario complete.");
    }

    private static async Task EnsureReadyAsync(
        HttpClient client,
        bool waitForReady,
        TextWriter error,
        TimeProvider timeProvider,
        CancellationToken cancellationToken)
    {
        using var timeout = waitForReady
            ? new CancellationTokenSource(TimeSpan.FromSeconds(30), timeProvider)
            : null;
        using var readiness = CancellationTokenSource.CreateLinkedTokenSource(
            cancellationToken, timeout?.Token ?? CancellationToken.None);

        try
        {
            while (true)
            {
                try
                {
                    using var response = await client.GetAsync("health", readiness.Token);
                    if (response.IsSuccessStatusCode)
                        return;

                    throw new HttpRequestException(
                        $"Demo readiness failed: GET /health returned HTTP {(int)response.StatusCode}.");
                }
                catch (HttpRequestException ex) when (waitForReady && !readiness.IsCancellationRequested)
                {
                    error.WriteLine($"Waiting for SliProductionDemo: {ex.Message}");
                }
                catch (OperationCanceledException) when (waitForReady && !readiness.IsCancellationRequested)
                {
                    error.WriteLine("Waiting for SliProductionDemo: GET /health timed out.");
                }

                await Task.Delay(TimeSpan.FromSeconds(1), timeProvider, readiness.Token);
            }
        }
        catch (OperationCanceledException ex) when (
            !cancellationToken.IsCancellationRequested && readiness.IsCancellationRequested)
        {
            throw new HttpRequestException("Demo readiness did not succeed within 30 seconds.", ex);
        }
    }

    private static async Task RunPhaseAsync(
        HttpClient client,
        Phase phase,
        GeneratorOptions options,
        Func<long> nextSequence,
        TextWriter output,
        TextWriter error,
        TimeProvider timeProvider,
        CancellationToken cancellationToken)
    {
        using var phaseCancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        var requestCounts = new ConcurrentDictionary<string, int>(StringComparer.Ordinal);
        var pending = new List<Task>(MaximumInFlightRequests);
        var duration = TimeSpan.FromSeconds(options.PhaseSeconds);
        var interval = TimeSpan.FromSeconds(1d / options.RequestsPerSecond);
        var started = timeProvider.GetTimestamp();
        var nextRequest = TimeSpan.Zero;
        var reportedBackpressure = false;
        ExceptionDispatchInfo? failure = null;

        output.WriteLine();
        output.WriteLine($"[{timeProvider.GetLocalNow():HH:mm:ss}] {phase.Name}");

        try
        {
            while (timeProvider.GetElapsedTime(started) < duration || pending.Count > 0)
            {
                phaseCancellation.Token.ThrowIfCancellationRequested();
                for (var i = pending.Count - 1; i >= 0; i--)
                {
                    if (!pending[i].IsCompleted)
                        continue;

                    var completed = pending[i];
                    pending.RemoveAt(i);
                    if (completed.Exception is { } fault)
                    {
                        failure = ExceptionDispatchInfo.Capture(fault.InnerExceptions[0]);
                        break;
                    }

                    await completed;
                }

                if (failure is not null)
                    break;

                var elapsed = timeProvider.GetElapsedTime(started);
                if (elapsed >= duration || pending.Count == MaximumInFlightRequests)
                {
                    if (elapsed < duration && !reportedBackpressure)
                    {
                        error.WriteLine(
                            $"In-flight limit ({MaximumInFlightRequests}) reached; slowing traffic until requests complete.");
                        reportedBackpressure = true;
                    }

                    if (pending.Count > 0)
                        await Task.WhenAny(pending).WaitAsync(phaseCancellation.Token);
                    continue;
                }

                if (elapsed < nextRequest)
                {
                    var remaining = duration - elapsed;
                    var delay = nextRequest - elapsed;
                    await WaitForCompletionOrDelayAsync(
                        pending, delay < remaining ? delay : remaining, timeProvider, phaseCancellation.Token);
                    continue;
                }

                pending.Add(SendRequestAsync(
                    client, phase.Kind, nextSequence(), options.Continuous, requestCounts, error, phaseCancellation.Token));

                nextRequest += interval;
                if (nextRequest < elapsed)
                    nextRequest = elapsed + interval;
            }
        }
        catch (HttpRequestException ex)
        {
            failure = ExceptionDispatchInfo.Capture(ex);
        }
        catch (OperationCanceledException ex)
        {
            failure = ExceptionDispatchInfo.Capture(ex);
        }
        finally
        {
            var stopping = Task.WhenAll(phaseCancellation.CancelAsync(), Task.WhenAll(pending));
            await stopping.ConfigureAwait(
                ConfigureAwaitOptions.ContinueOnCapturedContext | ConfigureAwaitOptions.SuppressThrowing);
            if (stopping.Exception is { } faults)
            {
                foreach (var exception in faults.Flatten().InnerExceptions)
                {
                    if (failure is null)
                        failure = ExceptionDispatchInfo.Capture(exception);
                    else
                        error.WriteLine($"Additional failure while stopping: {exception.GetType().Name}: {exception.Message}");
                }
            }
            else if (stopping.IsCanceled && failure is null)
            {
                failure = ExceptionDispatchInfo.Capture(new TaskCanceledException(stopping));
            }
        }

        // Shutdown wins over request failures discovered while draining.
        cancellationToken.ThrowIfCancellationRequested();
        failure?.Throw();

        var summary = string.Join(
            ", ",
            requestCounts.OrderBy(pair => pair.Key, StringComparer.Ordinal).Select(pair => $"{pair.Key}={pair.Value}"));
        output.WriteLine($"[{timeProvider.GetLocalNow():HH:mm:ss}] {phase.Name} complete: {summary}");
    }

    private static async Task WaitForCompletionOrDelayAsync(
        List<Task> pending, TimeSpan delay, TimeProvider timeProvider, CancellationToken cancellationToken)
    {
        using var wait = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        var deadline = Task.Delay(delay, timeProvider, wait.Token);
        try
        {
            var completed = await Task.WhenAny(pending.Append(deadline));
            if (completed == deadline)
                await deadline;
        }
        finally
        {
            await wait.CancelAsync();
        }
    }

    private static async Task SendRequestAsync(
        HttpClient client,
        ScenarioPhase phase,
        long requestNumber,
        bool continuous,
        ConcurrentDictionary<string, int> requestCounts,
        TextWriter error,
        CancellationToken cancellationToken)
    {
        using var request = ScenarioTraffic.CreateRequest(phase, requestNumber);
        try
        {
            using var response = await client.SendAsync(request, cancellationToken);
            var status = ((int)response.StatusCode).ToString(CultureInfo.InvariantCulture);
            requestCounts.AddOrUpdate(status, 1, static (_, count) => count + 1);
        }
        catch (HttpRequestException ex) when (continuous && !cancellationToken.IsCancellationRequested)
        {
            var count = requestCounts.AddOrUpdate("transport-failures", 1, static (_, count) => count + 1);
            if (count == 1)
                error.WriteLine(
                    $"Request {request.RequestUri} failed: {ex.Message} Continuing traffic. " +
                    "Further transport failures are included in this phase's summary.");
        }
        catch (OperationCanceledException ex) when (continuous && !cancellationToken.IsCancellationRequested)
        {
            var count = requestCounts.AddOrUpdate("timeouts", 1, static (_, count) => count + 1);
            if (count == 1)
                error.WriteLine(
                    $"Request {request.RequestUri} timed out: {ex.Message} Continuing traffic. " +
                    "Further timeouts are included in this phase's summary.");
        }
    }
}

internal static class ScenarioTraffic
{
    private static readonly string[] Projects = ["project-blue", "project-green", "project-red"];

    public static HttpRequestMessage CreateRequest(ScenarioPhase phase, long requestNumber)
    {
        var projectId = Projects[requestNumber % Projects.Length];
        return requestNumber % 4 == 0
            ? new(HttpMethod.Get, $"api/projects/{projectId}")
            : new(HttpMethod.Post, $"api/projects/{projectId}/sync?mode={SelectMode(phase, projectId, requestNumber)}");
    }

    private static string SelectMode(ScenarioPhase phase, string projectId, long requestNumber)
    {
        if (requestNumber % 5 == 0)
            return "healthy";

        return phase switch
        {
            ScenarioPhase.Incident when projectId == "project-red" =>
                requestNumber % 7 == 0 ? "failure" : "slow",
            ScenarioPhase.ClientError when projectId == "project-blue" && requestNumber % 2 == 0 => "client-error",
            ScenarioPhase.Throttled when projectId == "project-red" => "throttled",
            _ => "healthy",
        };
    }
}

internal sealed record GeneratorOptions(
    Uri BaseAddress, int PhaseSeconds, int RequestsPerSecond, bool Continuous = false, bool WaitForReady = false)
{
    public static GeneratorOptions Parse(string[] args)
    {
        var baseUrl = "http://localhost:5199/";
        var phaseSeconds = 30;
        var requestsPerSecond = 12;
        var continuous = false;
        var waitForReady = false;

        for (var i = 0; i < args.Length; i++)
        {
            switch (args[i])
            {
                case "--base-url":
                    baseUrl = ReadValue(args, ref i);
                    break;
                case "--phase-seconds":
                    phaseSeconds = ParsePositiveInt("--phase-seconds", ReadValue(args, ref i));
                    break;
                case "--requests-per-second":
                    requestsPerSecond = ParsePositiveInt("--requests-per-second", ReadValue(args, ref i));
                    break;
                case "--continuous":
                    continuous = true;
                    break;
                case "--wait-for-ready":
                    waitForReady = true;
                    break;
                default:
                    throw new ArgumentException($"Unknown argument '{args[i]}'.");
            }
        }

        if (!baseUrl.EndsWith('/'))
            baseUrl += "/";

        if (!Uri.TryCreate(baseUrl, UriKind.Absolute, out var address) ||
            (address.Scheme != Uri.UriSchemeHttp && address.Scheme != Uri.UriSchemeHttps))
            throw new ArgumentException("--base-url must be an absolute HTTP or HTTPS URL.");

        return new GeneratorOptions(address, phaseSeconds, requestsPerSecond, continuous, waitForReady);
    }

    private static string ReadValue(string[] args, ref int index)
    {
        if (++index >= args.Length || args[index].StartsWith("--", StringComparison.Ordinal))
            throw new ArgumentException($"Missing value for '{args[index - 1]}'.");

        return args[index];
    }

    private static int ParsePositiveInt(string name, string value) =>
        int.TryParse(value, out var parsed) && parsed > 0
            ? parsed
            : throw new ArgumentException($"{name} must be a positive integer.");
}

internal sealed record Phase(string Name, ScenarioPhase Kind);

internal enum ScenarioPhase
{
    Healthy,
    Incident,
    ClientError,
    Throttled,
    Recovery,
}
