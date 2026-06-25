using System.IO;
using Microsoft.AspNetCore.Mvc;
using QRD.Core.Services;

namespace QRD.Api.Controllers;

/// <summary>
/// Scanner API — equivalent to Python's /scanner/* routes.
/// </summary>
[ApiController]
[Route("scanner")]
public class ScannerController(ProjectScannerService scanner) : ControllerBase
{
    // ── Request / Response models ─────────────────────────────────────────────

    public record ScanRequest(
        string Path,
        List<string>? ExcludePatterns = null,
        List<string>? IncludePatterns = null);

    // ── Endpoints ─────────────────────────────────────────────────────────────

    [HttpPost("scan")]
    public async Task<IActionResult> ScanProject([FromBody] ScanRequest request, CancellationToken ct)
    {
        try
        {
            var result = await scanner.ScanAsync(
                request.Path,
                request.ExcludePatterns,
                ct: ct);

            return Ok(new
            {
                project_path = result.ProjectPath,
                project_name = result.ProjectName,
                file_tree    = SerializeTree(result.FileTree),
                flat_files   = result.FlatFiles.Select(SerializeFile),
                stats        = SerializeStats(result.Stats),
                scan_duration_ms = result.ScanDurationMs
            });
        }
        catch (DirectoryNotFoundException ex)
        {
            return NotFound(new { detail = ex.Message });
        }
        catch (Exception ex)
        {
            return StatusCode(500, new { detail = $"Scan failed: {ex.Message}" });
        }
    }

    [HttpGet("file-content")]
    public async Task<IActionResult> GetFileContent(
        [FromQuery] string path,
        [FromQuery] int maxLines = 5000,
        CancellationToken ct = default)
    {
        if (!System.IO.File.Exists(path))
            return NotFound(new { detail = "File not found" });

        try
        {
            var content = await System.IO.File.ReadAllTextAsync(path, ct);
            var lines = content.Split('\n');
            var truncated = lines.Length > maxLines;
            return Ok(new
            {
                content    = string.Join('\n', lines.Take(maxLines)),
                line_count = lines.Length,
                truncated,
                encoding   = "utf-8"
            });
        }
        catch (Exception ex)
        {
            return StatusCode(500, new { detail = $"Could not read file: {ex.Message}" });
        }
    }

    // ── Serializers ───────────────────────────────────────────────────────────

    private static object SerializeTree(Core.Domain.Entities.DirectoryNode node) => new
    {
        id           = node.Path,
        name         = node.Name,
        path         = node.Path,
        relative_path = node.RelativePath,
        type         = "directory",
        size         = node.TotalSize,
        extension    = "",
        last_modified = "",
        children     = node.ChildrenDirs.Select(SerializeTree)
                           .Concat(node.Files.Select(SerializeFile))
                           .ToList()
    };

    private static object SerializeFile(Core.Domain.Entities.FileInfo f) => new
    {
        id            = f.Id,
        name          = f.Name,
        path          = f.Path,
        relative_path = f.RelativePath,
        type          = "file",
        size          = f.SizeBytes,
        line_count    = f.LineCount,
        language      = f.Language?.ToString(),
        extension     = f.Extension,
        last_modified = f.LastModified.ToString("O"),
        category      = f.Category.ToString().ToLower(),
        is_binary     = f.IsBinary
    };

    private static object SerializeStats(Core.Domain.Entities.ProjectStats s) => new
    {
        total_files            = s.TotalFiles,
        total_directories      = s.TotalDirectories,
        total_lines            = s.TotalLines,
        total_size             = s.TotalSize,
        language_distribution  = s.LanguageDistribution,
        extension_distribution = s.ExtensionDistribution,
        largest_files          = s.LargestFiles.Select(f => new {
            path = f.Path, size = f.Size, lines = f.Lines
        }),
        average_file_size      = s.AverageFileSize,
        average_line_count     = s.AverageLineCount
    };
}
