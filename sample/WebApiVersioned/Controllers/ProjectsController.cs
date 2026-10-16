namespace SampleVersionedWebApplicationSLI.Controllers;

using Asp.Versioning;
using Microsoft.AspNetCore.Mvc;
using Trellis.ServiceLevelIndicators;

/// <summary>
/// Demonstrates the same measured operation across two API versions.
/// </summary>
[ApiController]
[ApiVersion("1.0")]
[ApiVersion("2.0")]
[Route("projects/{projectId:regex(^project-(blue|green|red)$)}")]
[ServiceLevelIndicator(Operation = "GetProject")]
public sealed class ProjectsController : ControllerBase
{
    /// <summary>
    /// Gets a project with its resolved API version.
    /// </summary>
    /// <param name="projectId">The stable target resource.</param>
    [HttpGet]
    public IActionResult Get([CustomerResourceId] string projectId) =>
        Ok(new { projectId, apiVersion = HttpContext.ApiVersioningFeature.RequestedApiVersion?.ToString() });
}
