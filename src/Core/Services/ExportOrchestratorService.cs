using QRD.Core.Domain.Entities;
using QRD.Core.Infrastructure.AI;
using QRD.Core.Infrastructure.PDF;
using FileInfo = QRD.Core.Domain.Entities.FileInfo;

namespace QRD.Core.Services;

/// <summary>
/// Orchestrates the complete export pipeline.
/// Equivalent to Python's ExportOrchestratorService.
/// Supports all four export modes: Single, Folder, File, Package.
/// </summary>
public class ExportOrchestratorService
{
    private readonly ProjectScannerService _scanner;
    private readonly AIDocumenter _aiDocumenter;
    private readonly PdfBuilder _pdfBuilder;

    // Active jobs registry
    private readonly Dictionary<string, ExportJob> _jobs = new();
    private readonly Dictionary<string, CancellationTokenSource> _cancellations = new();

    public ExportOrchestratorService(
        ProjectScannerService scanner,
        AIDocumenter aiDocumenter,
        PdfBuilder pdfBuilder)
    {
        _scanner = scanner;
        _aiDocumenter = aiDocumenter;
        _pdfBuilder = pdfBuilder;
    }

    // ── Public API ─────────────────────────────────────────────────────────────

    /// <summary>
    /// Start an export job asynchronously. Returns job_id for tracking.
    /// </summary>
    public string StartExport(
        string projectPath,
        string outputPath,
        ExportMode mode,
        ExportOptions options,
        IEnumerable<string>? excludePatterns = null)
    {
        var job = new ExportJob
        {
            ProjectPath = projectPath,
            Mode = mode,
            OutputPath = outputPath,
            Status = "pending",
            StartedAt = DateTime.UtcNow
        };

        var cts = new CancellationTokenSource();
        _jobs[job.Id] = job;
        _cancellations[job.Id] = cts;

        _ = RunExportAsync(job, options, excludePatterns?.ToList() ?? [], cts.Token);

        return job.Id;
    }

    public void CancelExport(string jobId)
    {
        if (_cancellations.TryGetValue(jobId, out var cts))
        {
            cts.Cancel();
            if (_jobs.TryGetValue(jobId, out var job))
                job.Status = "cancelled";
        }
    }

    public ExportJob? GetJob(string jobId)
        => _jobs.GetValueOrDefault(jobId);

    // ── Pipeline ──────────────────────────────────────────────────────────────

    private async Task RunExportAsync(
        ExportJob job,
        ExportOptions options,
        List<string> excludePatterns,
        CancellationToken ct)
    {
        try
        {
            job.Status = "running";
            job.CurrentFile = "Scanning project…";

            // Step 1 — scan
            var scanProgress = new Progress<(double Percent, string Message)>(t =>
            {
                job.Progress = t.Percent * 0.20;
                job.CurrentFile = t.Message;
            });

            var scan = await _scanner.ScanAsync(
                job.ProjectPath, excludePatterns, scanProgress, ct);

            job.TotalFiles = scan.FlatFiles.Count;
            job.Progress = 20;
            job.CurrentFile = $"Found {job.TotalFiles} files";

            // Step 2 — AI summaries (optional)
            var aiDocs = new Dictionary<string, AIDocumentation>();
            if (options.IncludeAi && _aiDocumenter.IsAvailable)
            {
                job.CurrentFile = "Generating AI summaries…";
                aiDocs = await GenerateAiSummariesAsync(scan.FlatFiles, job, ct);
                job.Progress = 45;
            }

            // Step 3 — build PDFs
            Directory.CreateDirectory(job.OutputPath);
            var buildProgress = new Progress<(double Percent, string Message)>(t =>
            {
                job.Progress = 45 + t.Percent * 0.50;
                job.CurrentFile = t.Message;
                job.ProcessedFiles = (int)(t.Percent / 100 * job.TotalFiles);
            });

            var outputFiles = job.Mode switch
            {
                ExportMode.Single => [await _pdfBuilder.BuildSinglePdfAsync(
                    scan, aiDocs, options,
                    Path.Combine(job.OutputPath, $"{scan.ProjectName}.pdf"),
                    buildProgress, ct)],

                ExportMode.Folder => await _pdfBuilder.BuildFolderPdfsAsync(
                    scan, aiDocs, options, job.OutputPath, buildProgress, ct),

                ExportMode.File => await BuildPerFilePdfsAsync(
                    scan, aiDocs, options, job, buildProgress, ct),

                ExportMode.Package => await BuildPackageAsync(
                    scan, aiDocs, options, job, buildProgress, ct),

                _ => throw new ArgumentOutOfRangeException(nameof(job.Mode))
            };

            job.OutputFiles = outputFiles;
            job.Progress = 100;
            job.Status = "completed";
            job.CompletedAt = DateTime.UtcNow;
        }
        catch (OperationCanceledException)
        {
            job.Status = "cancelled";
        }
        catch (Exception ex)
        {
            job.Status = "failed";
            job.Error = ex.Message;
        }
    }

    private async Task<Dictionary<string, AIDocumentation>> GenerateAiSummariesAsync(
        List<FileInfo> files,
        ExportJob job,
        CancellationToken ct)
    {
        var result = new Dictionary<string, AIDocumentation>();
        var sourceFiles = files.Where(f => f.Category == FileCategory.Source && !f.IsBinary).ToList();
        var done = 0;

        // Throttle to avoid rate limits
        var semaphore = new SemaphoreSlim(3);

        await Task.WhenAll(sourceFiles.Select(async file =>
        {
            await semaphore.WaitAsync(ct);
            try
            {
                ct.ThrowIfCancellationRequested();
                var content = await File.ReadAllTextAsync(file.Path, ct);
                var doc = await _aiDocumenter.DocumentFileAsync(
                    file.Id, file.RelativePath, content,
                    file.Language?.ToString() ?? "Unknown", ct: ct);
                lock (result) result[file.Id] = doc;

                done++;
                job.CurrentFile = $"AI: {file.RelativePath} ({done}/{sourceFiles.Count})";
            }
            finally
            {
                semaphore.Release();
            }
        }));

        return result;
    }

    private async Task<List<string>> BuildPerFilePdfsAsync(
        ProjectScan scan,
        Dictionary<string, AIDocumentation> aiDocs,
        ExportOptions options,
        ExportJob job,
        IProgress<(double, string)>? progress,
        CancellationToken ct)
    {
        var outputs = new List<string>();
        var files = scan.FlatFiles;
        var done = 0;

        foreach (var file in files)
        {
            ct.ThrowIfCancellationRequested();
            var singleFileScan = new ProjectScan
            {
                ProjectPath = scan.ProjectPath,
                ProjectName = scan.ProjectName,
                ScannedAt = scan.ScannedAt,
                FileTree = scan.FileTree,
                FlatFiles = [file],
                Stats = scan.Stats
            };

            var safeName = file.RelativePath.Replace('/', '_').Replace('\\', '_');
            var outPath = Path.Combine(job.OutputPath, $"{safeName}.pdf");
            var singleAiDocs = aiDocs.ContainsKey(file.Id)
                ? new Dictionary<string, AIDocumentation> { [file.Id] = aiDocs[file.Id] }
                : new Dictionary<string, AIDocumentation>();

            var outFile = await _pdfBuilder.BuildSinglePdfAsync(
                singleFileScan, singleAiDocs, options, outPath, ct: ct);
            outputs.Add(outFile);
            done++;
            progress?.Report((done / (double)files.Count * 100, file.RelativePath));
        }

        return outputs;
    }

    private async Task<List<string>> BuildPackageAsync(
        ProjectScan scan,
        Dictionary<string, AIDocumentation> aiDocs,
        ExportOptions options,
        ExportJob job,
        IProgress<(double, string)>? progress,
        CancellationToken ct)
    {
        var outputs = new List<string>();

        // Main combined PDF
        var mainPath = Path.Combine(job.OutputPath, $"{scan.ProjectName}-full.pdf");
        var mainOut = await _pdfBuilder.BuildSinglePdfAsync(scan, aiDocs, options, mainPath, ct: ct);
        outputs.Add(mainOut);

        // Per-folder PDFs
        var folderOuts = await _pdfBuilder.BuildFolderPdfsAsync(
            scan, aiDocs, options,
            Path.Combine(job.OutputPath, "modules"),
            progress, ct);
        outputs.AddRange(folderOuts);

        return outputs;
    }
}
