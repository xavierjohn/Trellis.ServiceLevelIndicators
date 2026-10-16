namespace Trellis.ServiceLevelIndicators.Asp.ApiVersioning.Tests;

using global::Asp.Versioning;
using Microsoft.AspNetCore.Mvc;

[ApiController]
[Route("[controller]")]
[ApiVersion("2023-08-29")]
public class TestSingleController : ControllerBase
{
    [HttpGet]
    public IActionResult Get() => Ok("Hello World!");

    [HttpGet("abort/{id}")]
    public IActionResult Abort([CustomerResourceId] string id)
    {
        HttpContext.Abort();
        throw new OperationCanceledException(id, HttpContext.RequestAborted);
    }
}
