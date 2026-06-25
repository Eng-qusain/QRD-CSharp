using Microsoft.AspNetCore.Mvc;
using QRD.Core.Domain.Entities;
using QRD.Core.Infrastructure.PDF;
using QRD.Core.Services;

namespace QRD.Api.Controllers;

/// <summary>
/// Export API — equivalent to Python's /export/* routes.
/// v2: ExportOptions properties now use strongly-typed enums instead of raw strings.
/// </summary>
[ApiController]
[Route("export")]
public class ExportController(ExportOrchestratorService orchestrator) : ControllerBase
{
    // ── Request / Response models ─────────────────────────────────────────────

    /// <summary>
    /// Incoming JSON still uses plain strings for easy HTTP consumption.
    /// They are parsed to enums before being passed to the domain layer.
    /// </summary>
    public record ExportOptionsRequest(
        string Mode        = "single",
        string OutputPath  = "",
        bool IncludeAi     = true,
        bool IncludeCharts = true,
        bool IncludeToc    = true,
        bool IncludeStats  = true,
        bool SyntaxHighlighting = true,
        bool LineNumbers   = true,
        int MaxCsvRows     = 100,
        // v2: explicit string literals matching union types
        string PaperSize   = "A4",           // "A4" | "Letter" | "A3"
        string Orientation  = "portrait",     // "portrait" | "landscape"
        string Theme        = "default",      // "default" | "dark" | "github" | "monokai"
        int FontSize        = 9,
        List<string>? SelectedFiles = null);

    public record StartExportRequest(
        string ProjectPath,
        ExportOptionsRequest Options,
        List<string>? ExcludePatterns = null);

    // ── Endpoints ─────────────────────────────────────────────────────────────

    [HttpPost("start")]
    public IActionResult StartExport([FromBody] StartExportRequest request)
    {
        if (!Enum.TryParse<ExportMode>(request.Options.Mode, ignoreCase: true, out var mode))
            return BadRequest(new { detail = $"Invalid export mode: {request.Options.Mode}. Valid: single, folder, file, package" });

        // v2: parse string literals to proper enums (replaces TypeScript "as any" casts)
        if (!Enum.TryParse<PaperSize>(request.Options.PaperSize, ignoreCase: true, out var paperSize))
            paperSize = PaperSize.A4;

        if (!Enum.TryParse<PdfOrientation>(request.Options.Orientation, ignoreCase: true, out var orientation))
            orientation = PdfOrientation.Portrait;

        if (!Enum.TryParse<PdfThemeName>(request.Options.Theme, ignoreCase: true, out var theme))
            theme = PdfThemeName.Default;

        var options = new ExportOptions
        {
            Mode               = mode,
            IncludeAi          = request.Options.IncludeAi,
            IncludeCharts      = request.Options.IncludeCharts,
            IncludeToc         = request.Options.IncludeToc,
            IncludeStats       = request.Options.IncludeStats,
            SyntaxHighlighting = request.Options.SyntaxHighlighting,
            LineNumbers        = request.Options.LineNumbers,
            MaxCsvRows         = request.Options.MaxCsvRows,
            PaperSize          = paperSize,
            Orientation        = orientation,
            Theme              = theme,
            FontSize           = request.Options.FontSize,
            SelectedFiles      = request.Options.SelectedFiles ?? []
        };

        var jobId = orchestrator.StartExport(
            request.ProjectPath,
            request.Options.OutputPath,
            mode,
            options,
            request.ExcludePatterns);

        return Ok(new { job_id = jobId, message = "Export started" });
    }

    [HttpGet("{jobId}/status")]
    public IActionResult GetJobStatus(string jobId)
    {
        var job = orchestrator.GetJob(jobId);
        if (job is null)
            return NotFound(new { detail = $"Job not found: {jobId}" });

        return Ok(new
        {
            id               = job.Id,
            status           = job.Status,
            progress         = job.Progress,
            current_file     = job.CurrentFile,
            total_files      = job.TotalFiles,
            processed_files  = job.ProcessedFiles,
            output_files     = job.OutputFiles,
            error            = job.Error,
            started_at       = job.StartedAt?.ToString("O"),
            completed_at     = job.CompletedAt?.ToString("O"),
            estimated_remaining_seconds = job.EstimatedRemainingSeconds
        });
    }

    [HttpPost("{jobId}/cancel")]
    public IActionResult CancelExport(string jobId)
    {
        orchestrator.CancelExport(jobId);
        return Ok(new { message = $"Job {jobId} cancellation requested" });
    }
}
