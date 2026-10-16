namespace SliProductionDemo.Controllers;

using Microsoft.AspNetCore.Mvc;
using Trellis.ServiceLevelIndicators;

/// <summary>
/// Project Tracker endpoints with repeatable operating modes and bounded latency jitter.
/// </summary>
[ApiController]
[Route("api/projects")]
[Produces("application/json")]
public sealed class ProjectsController : ControllerBase
{
    internal const string UnknownProjectId = "project-unknown";
    private static readonly string[] KnownProjects = ["project-blue", "project-green", "project-red"];

    /// <summary>
    /// Returns one project using a consistently fast path.
    /// </summary>
    [HttpGet("{projectId}")]
    [ServiceLevelIndicator(Operation = "GetProject")]
    public async Task<IActionResult> Get(
        [CustomerResourceId] string projectId,
        CancellationToken cancellationToken)
    {
        if (!KnownProjects.Contains(projectId, StringComparer.Ordinal))
            return ProjectNotFound(projectId);

        await Task.Delay(NextDelay("read"), cancellationToken);
        return Ok(new { projectId, status = "Active" });
    }

    /// <summary>
    /// Synchronizes one project using a deterministic operating mode.
    /// </summary>
    [HttpPost("{projectId}/sync")]
    [ServiceLevelIndicator(Operation = "SyncProject")]
    public async Task<IActionResult> Sync(
        [CustomerResourceId] string projectId,
        [FromQuery] string mode = "healthy",
        CancellationToken cancellationToken = default)
    {
        if (!KnownProjects.Contains(projectId, StringComparer.Ordinal))
            return ProjectNotFound(projectId);

        switch (mode)
        {
            case "healthy":
                await Task.Delay(NextDelay(mode), cancellationToken);
                return Ok(new { projectId, mode, synchronized = true });

            case "slow":
                await Task.Delay(NextDelay(mode), cancellationToken);
                return Ok(new { projectId, mode, synchronized = true });

            case "client-error":
                await Task.Delay(NextDelay(mode), cancellationToken);
                return BadRequest(new { projectId, mode, error = "The requested sync range is invalid." });

            case "throttled":
                await Task.Delay(NextDelay(mode), cancellationToken);
                return StatusCode(
                    StatusCodes.Status429TooManyRequests,
                    new { projectId, mode, error = "Sync capacity is temporarily exhausted." });

            case "failure":
                await Task.Delay(NextDelay(mode), cancellationToken);
                return StatusCode(
                    StatusCodes.Status503ServiceUnavailable,
                    new { projectId, mode, error = "The downstream sync service is unavailable." });

            default:
                return BadRequest(new { projectId, mode, error = "Unknown demo mode." });
        }
    }

    private NotFoundObjectResult ProjectNotFound(string projectId)
    {
        if (HttpContext.TryGetMeasuredOperation(out var measuredOperation))
            measuredOperation.CustomerResourceId = UnknownProjectId;

        return NotFound(new { projectId, error = "Project not found." });
    }

    internal static int NextDelay(string mode)
    {
        ReadOnlySpan<int> values = mode switch
        {
            "read" => [25, 35, 45, 55],
            "healthy" => [45, 60, 75, 90],
            "slow" => [550, 700, 850, 1_000],
            "client-error" => [25, 35, 45],
            "throttled" => [35, 50, 65],
            "failure" => [450, 600, 750],
            _ => throw new ArgumentOutOfRangeException(nameof(mode), mode, "Unknown demo latency mode."),
        };
        return values[Random.Shared.Next(values.Length)];
    }
}
