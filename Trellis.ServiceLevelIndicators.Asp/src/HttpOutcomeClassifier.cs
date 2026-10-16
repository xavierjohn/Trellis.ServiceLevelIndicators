namespace Trellis.ServiceLevelIndicators;

using Microsoft.AspNetCore.Http;

/// <summary>
/// The built-in HTTP response classification used by SLI middleware.
/// Request cancellation and escaping exceptions are handled separately by the middleware.
/// </summary>
public static class HttpOutcomeClassifier
{
    /// <summary>
    /// Maps 2xx/3xx to success, selected caller errors to client error, 429 and 5xx to failure,
    /// and other status codes to ignored.
    /// </summary>
    public static SliOutcome ClassifyStatusCode(int statusCode) => statusCode switch
    {
        >= StatusCodes.Status200OK and < StatusCodes.Status400BadRequest => SliOutcome.Success,
        StatusCodes.Status400BadRequest
            or StatusCodes.Status401Unauthorized
            or StatusCodes.Status403Forbidden
            or StatusCodes.Status404NotFound
            or StatusCodes.Status409Conflict
            or StatusCodes.Status412PreconditionFailed
            or StatusCodes.Status422UnprocessableEntity => SliOutcome.ClientError,
        StatusCodes.Status429TooManyRequests => SliOutcome.Failure,
        >= StatusCodes.Status500InternalServerError => SliOutcome.Failure,
        _ => SliOutcome.Ignored,
    };
}
