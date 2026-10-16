namespace Trellis.ServiceLevelIndicators;

using Microsoft.AspNetCore.Http;

/// <summary>
/// ASP.NET Core-specific SLI options.
/// </summary>
public sealed class ServiceLevelIndicatorHttpOptions
{
    /// <summary>
    /// Optional replacement classifier for normally completed HTTP requests.
    /// Abort and escaping-exception handling take precedence.
    /// Used only when <see cref="ClassifyOutcomeWithDefault"/> is not set.
    /// Callback exceptions and undefined outcomes are logged and fall back to built-in status classification.
    /// Fallback can change the SLO population and emits an EventSource diagnostic even when warning logs are filtered.
    /// </summary>
    public Func<HttpContext, SliOutcome>? ClassifyOutcome { get; set; }

    /// <summary>
    /// Optional classifier receiving the HTTP context and built-in status-code outcome.
    /// Applies only to normally completed requests; abort and escaping-exception handling take precedence.
    /// Takes precedence over <see cref="ClassifyOutcome"/> if both properties are set directly.
    /// The fluent registration overloads clear the other property, so the last registration wins.
    /// Callback exceptions and undefined outcomes are logged and fall back to built-in status classification.
    /// Fallback can change the SLO population and emits an EventSource diagnostic even when warning logs are filtered.
    /// </summary>
    public Func<HttpContext, SliOutcome, SliOutcome>? ClassifyOutcomeWithDefault { get; set; }
}
