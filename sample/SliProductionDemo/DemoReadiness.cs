namespace SliProductionDemo;

using System.Net;
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
            urls = string.Join(';',
                ExpandPorts(configuration[WebHostDefaults.HttpPortsKey], Uri.UriSchemeHttp)
                    .Concat(ExpandPorts(configuration[WebHostDefaults.HttpsPortsKey], Uri.UriSchemeHttps)));
        }

        var endpoints = configuration.GetSection("Kestrel:Endpoints").GetChildren()
            .Select(endpoint => endpoint["Url"]
                ?? throw new InvalidOperationException($"Kestrel endpoint '{endpoint.Key}' requires a Url."))
            .ToArray();
        if (endpoints.Length > 0 &&
            (!configuration.GetValue<bool>(WebHostDefaults.PreferHostingUrlsKey) || string.IsNullOrWhiteSpace(urls)))
            urls = string.Join(';', endpoints);

        if (string.IsNullOrWhiteSpace(urls))
            urls = "http://localhost:5000";

        var address = urls.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(BindingAddress.Parse)
            .FirstOrDefault(binding => string.Equals(binding.Scheme, Uri.UriSchemeHttp, StringComparison.OrdinalIgnoreCase))
            ?? throw new InvalidOperationException(
                "The demo readiness probe requires an HTTP listener configured with urls, http_ports, or Kestrel:Endpoints.");

        if (address.IsUnixPipe || address.IsNamedPipe || address.Port is < 1 or > 65_535 ||
            !string.IsNullOrEmpty(address.PathBase))
            throw new InvalidOperationException(
                "The demo readiness probe requires an HTTP listener on a fixed TCP port (1-65535), without a path base or pipe.");

        var host = address.Host;
        if (host is "+" or "*" || Uri.CheckHostName(host) == UriHostNameType.Dns)
            host = "localhost";
        else if (IPAddress.TryParse(host, out var ipAddress) &&
            (IPAddress.Any.Equals(ipAddress) || IPAddress.IPv6Any.Equals(ipAddress)))
            host = "localhost";

        return new UriBuilder(Uri.UriSchemeHttp, host, address.Port, Path).Uri;

        static IEnumerable<string> ExpandPorts(string? ports, string scheme) =>
            (ports ?? "").Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .Select(port => $"{scheme}://+:{port}");
    }
}
