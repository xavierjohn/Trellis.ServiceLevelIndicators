namespace SliProductionDemo;

internal static class DemoReadiness
{
    internal const string Path = "/health";

    internal static async Task<int> CheckAsync(
        HttpClient client, string[] args, TextWriter error, CancellationToken cancellationToken = default)
    {
        if (args is not ["--health-check", var target] ||
            !Uri.TryCreate(target, UriKind.Absolute, out var address) ||
            (address.Scheme != Uri.UriSchemeHttp && address.Scheme != Uri.UriSchemeHttps) ||
            address.Port < 1)
        {
            error.WriteLine("Usage: SliProductionDemo --health-check <absolute HTTP or HTTPS URL>");
            return 2;
        }

        try
        {
            using var response = await client.GetAsync(address, cancellationToken);
            if (response.IsSuccessStatusCode)
                return 0;

            error.WriteLine($"Demo readiness check failed: HTTP {(int)response.StatusCode}.");
        }
        catch (HttpRequestException ex)
        {
            error.WriteLine($"Demo readiness check failed: {ex.Message}");
        }
        catch (OperationCanceledException ex)
        {
            error.WriteLine($"Demo readiness check timed out: {ex.Message}");
        }

        return 1;
    }
}
