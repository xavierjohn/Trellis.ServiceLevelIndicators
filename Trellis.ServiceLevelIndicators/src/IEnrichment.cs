namespace Trellis.ServiceLevelIndicators;

using System.Threading.Tasks;

/// <summary>
/// Defines an enrichment that adds additional attributes to a measurement context.
/// </summary>
/// <typeparam name="T">The enrichment context type.</typeparam>
public interface IEnrichment<T>
    where T : IEnrichmentContext
{
    /// <summary>
    /// Whether the ASP.NET Core integration may run this enrichment after request cancellation.
    /// Defaults to <see langword="false"/> for existing implementations.
    /// Only opt in for bounded, cancellation-safe metadata access that does not perform I/O.
    /// The canceled request token is still passed to <see cref="EnrichAsync"/>.
    /// </summary>
    bool EnrichOnCancellation => false;

    /// <summary>
    /// Enriches the measurement context with additional attributes.
    /// </summary>
    /// <param name="context">The enrichment context.</param>
    /// <param name="cancellationToken">A cancellation token.</param>
    /// <returns>A <see cref="ValueTask"/> representing the asynchronous operation.</returns>
    ValueTask EnrichAsync(T context, CancellationToken cancellationToken);
}
