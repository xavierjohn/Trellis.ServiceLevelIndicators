using System.Collections.Concurrent;
using System.Diagnostics;
using System.Runtime.InteropServices;

var options = GeneratorOptions.Parse(args);
using var cancellation = new CancellationTokenSource();
ConsoleCancelEventHandler cancelHandler = (_, eventArgs) =>
{
    eventArgs.Cancel = true;
    cancellation.Cancel();
};
Console.CancelKeyPress += cancelHandler;
using var termination = OperatingSystem.IsLinux()
    ? PosixSignalRegistration.Create(PosixSignal.SIGTERM, context =>
    {
        context.Cancel = true;
        cancellation.Cancel();
    })
    : null;

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
    await ScenarioRunner.RunAsync(client, options, Console.Out, Console.Error, cancellation.Token);
}
catch (OperationCanceledException) when (cancellation.IsCancellationRequested)
{
    Console.WriteLine("Scenario stopped.");
    return 0;
}
catch (HttpRequestException ex)
{
    Console.Error.WriteLine(
        $"Scenario stopped: an HTTP request to {client.BaseAddress} failed. " +
        $"Start SliProductionDemo, check --base-url, and try again. {ex.Message}");
    return 1;
}
catch (TaskCanceledException ex)
{
    Console.Error.WriteLine(
        $"Scenario stopped: an HTTP request to {client.BaseAddress} timed out. {ex.Message}");
    return 1;
}
finally
{
    Console.CancelKeyPress -= cancelHandler;
}

return 0;

internal static class ScenarioRunner
{
    private static readonly Phase[] Phases =
    [
        new("Healthy baseline", ScenarioPhase.Healthy),
        new("Targeted project-red incident", ScenarioPhase.Incident),
        new("Client-error burst", ScenarioPhase.ClientError),
        new("Capacity pressure", ScenarioPhase.Throttled),
        new("Recovery", ScenarioPhase.Recovery),
    ];

    public static async Task RunAsync(
        HttpClient client,
        GeneratorOptions options,
        TextWriter output,
        TextWriter error,
        CancellationToken cancellationToken)
    {
        await EnsureReadyAsync(client, options.WaitForReady, error, cancellationToken);

        long sequence = 0;
        long cycle = 0;
        do
        {
            cancellationToken.ThrowIfCancellationRequested();
            output.WriteLine($"Cycle {++cycle}");
            foreach (var phase in Phases)
            {
                await RunPhaseAsync(
                    client, phase, options, () => Interlocked.Increment(ref sequence), output, cancellationToken);
            }
        }
        while (options.Continuous);

        output.WriteLine("Scenario complete.");
    }

    private static async Task EnsureReadyAsync(
        HttpClient client, bool waitForReady, TextWriter error, CancellationToken cancellationToken)
    {
        using var readiness = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        if (waitForReady)
            readiness.CancelAfter(TimeSpan.FromSeconds(30));

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
                catch (HttpRequestException ex) when (waitForReady)
                {
                    error.WriteLine($"Waiting for SliProductionDemo: {ex.Message}");
                }

                await Task.Delay(TimeSpan.FromSeconds(1), readiness.Token);
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
        CancellationToken cancellationToken)
    {
        var statusCounts = new ConcurrentDictionary<int, int>();
        var pending = new List<Task>();
        var duration = TimeSpan.FromSeconds(options.PhaseSeconds);
        var interval = TimeSpan.FromSeconds(1d / options.RequestsPerSecond);
        var stopwatch = Stopwatch.StartNew();

        output.WriteLine();
        output.WriteLine($"[{DateTimeOffset.Now:HH:mm:ss}] {phase.Name}");

        try
        {
            while (stopwatch.Elapsed < duration)
            {
                cancellationToken.ThrowIfCancellationRequested();
                pending.Add(SendRequestAsync(client, phase.Kind, nextSequence(), statusCounts, cancellationToken));
                await Task.Delay(interval, cancellationToken);
            }
        }
        finally
        {
            await Task.WhenAll(pending);
        }

        var summary = string.Join(
            ", ",
            statusCounts.OrderBy(pair => pair.Key).Select(pair => $"{pair.Key}={pair.Value}"));
        output.WriteLine($"[{DateTimeOffset.Now:HH:mm:ss}] {phase.Name} complete: {summary}");
    }

    private static async Task SendRequestAsync(
        HttpClient client,
        ScenarioPhase phase,
        long requestNumber,
        ConcurrentDictionary<int, int> statusCounts,
        CancellationToken cancellationToken)
    {
        using var request = ScenarioTraffic.CreateRequest(phase, requestNumber);
        using var response = await client.SendAsync(request, cancellationToken);
        statusCounts.AddOrUpdate((int)response.StatusCode, 1, (_, count) => count + 1);
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
        if (++index >= args.Length)
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
