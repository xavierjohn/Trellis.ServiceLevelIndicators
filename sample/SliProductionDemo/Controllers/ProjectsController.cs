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
    private static readonly int[] ReadDelays = [25, 35, 45, 55];
    private static readonly Dictionary<string, SyncMode> SyncModes = new(StringComparer.Ordinal)
    {
        ["healthy"] = new(StatusCodes.Status200OK, [45, 60, 75, 90]),
        ["slow"] = new(StatusCodes.Status200OK, [550, 700, 850, 1_000]),
        ["client-error"] = new(StatusCodes.Status400BadRequest, [25, 35, 45], "The requested sync range is invalid."),
        ["throttled"] = new(StatusCodes.Status429TooManyRequests, [35, 50, 65], "Sync capacity is temporarily exhausted."),
        ["failure"] = new(StatusCodes.Status503ServiceUnavailable, [450, 600, 750], "The downstream sync service is unavailable."),
    };

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

        if (!SyncModes.TryGetValue(mode, out var scenario))
            return BadRequest(new { projectId, mode, error = "Unknown demo mode." });

        await Task.Delay(NextDelay(mode), cancellationToken);
        return scenario.Error is null
            ? StatusCode(scenario.StatusCode, new { projectId, mode, synchronized = true })
            : StatusCode(scenario.StatusCode, new { projectId, mode, error = scenario.Error });
    }

    private NotFoundObjectResult ProjectNotFound(string projectId)
    {
        if (HttpContext.TryGetMeasuredOperation(out var measuredOperation))
            measuredOperation.CustomerResourceId = UnknownProjectId;

        return NotFound(new { projectId, error = "Project not found." });
    }

    internal static int NextDelay(string mode)
    {
        var values = mode == "read" ? ReadDelays
            : SyncModes.TryGetValue(mode, out var scenario) ? scenario.Delays
            : throw new ArgumentOutOfRangeException(nameof(mode), mode, "Unknown demo latency mode.");
        return values[Random.Shared.Next(values.Length)];
    }

    private sealed record SyncMode(int StatusCode, int[] Delays, string? Error = null);
}
