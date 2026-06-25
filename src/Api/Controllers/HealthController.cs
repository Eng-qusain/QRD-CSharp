using Microsoft.AspNetCore.Mvc;
using QRD.Core.Infrastructure.AI;
using QRD.Utils;

namespace QRD.Api.Controllers;

[ApiController]
public class HealthController(AppSettings settings, AIDocumenter ai) : ControllerBase
{
    [HttpGet("/")]
    public IActionResult Root() => Ok(new
    {
        name    = "QRD — Quantum Repo Documenter API",
        version = "1.0.0",
        status  = "running",
        endpoints = new
        {
            scanner = "/scanner/scan",
            export  = "/export/start",
            ai_status = "/ai/status",
            health  = "/health"
        }
    });

    [HttpGet("/health")]
    public IActionResult Health() => Ok(new
    {
        status = "ok",
        ai_available = ai.IsAvailable,
        env    = settings.Env
    });

    [HttpGet("/ai/status")]
    public IActionResult AiStatus() => Ok(new
    {
        available = ai.IsAvailable,
        model     = settings.AiModel,
        provider  = string.IsNullOrEmpty(settings.AnthropicApiKey) ? "none" : "anthropic"
    });
}
