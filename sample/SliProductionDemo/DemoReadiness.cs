namespace SliProductionDemo;

using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;

internal static class DemoReadiness
{
    internal const string Path = "/health";

    internal static Uri GetUri(IConfiguration configuration)
    {
        var urls = configuration[WebHostDefaults.ServerUrlsKey];
        if (string.IsNullOrWhiteSpace(urls))
        {
            var ports = configuration[WebHostDefaults.HttpPortsKey];
            urls = string.IsNullOrWhiteSpace(ports)
                ? "http://localhost:5000"
                : string.Join(';', ports.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                    .Select(port => $"http://+:{port}"));
        }

        var address = urls.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(BindingAddress.Parse)
            .FirstOrDefault(binding => string.Equals(binding.Scheme, Uri.UriSchemeHttp, StringComparison.OrdinalIgnoreCase))
            ?? throw new InvalidOperationException(
                "The demo readiness probe requires an HTTP listener configured with urls or http_ports.");

        var host = address.Host is "+" or "*" or "0.0.0.0" or "[::]" ? "localhost" : address.Host;
        return new UriBuilder(Uri.UriSchemeHttp, host, address.Port, Path).Uri;
    }
}
