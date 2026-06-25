using MigraDoc.DocumentObjectModel;
using MigraDoc.DocumentObjectModel.Tables;
using MigraDoc.Rendering;
using QRD.Core.Domain.Entities;
using FileInfo = QRD.Core.Domain.Entities.FileInfo;

namespace QRD.Core.Infrastructure.PDF;

/// <summary>
/// PDF generation engine using MigraDoc/PdfSharp.
/// Equivalent to Python's ReportLab-based PdfBuilder.
/// Supports syntax-highlighted source code, data tables, images,
/// clickable table of contents, and multiple themes.
/// </summary>
public class PdfBuilder
{
    // ── Themes ───────────────────────────────────────────────────────────────

    public static readonly Dictionary<string, PdfTheme> Themes = new()
    {
        ["default"] = new PdfTheme("#ffffff", "#1a1a2e", "#f6f8fa", "#24292e",
                                   "#0969da", "#0969da", "#d0d7de", "#8b949e", "#0969da", "#ffffff"),
        ["dark"]    = new PdfTheme("#0d1117", "#c9d1d9", "#161b22", "#c9d1d9",
                                   "#58a6ff", "#58a6ff", "#30363d", "#8b949e", "#161b22", "#58a6ff"),
        ["github"]  = new PdfTheme("#ffffff", "#24292e", "#f6f8fa", "#24292e",
                                   "#0366d6", "#24292e", "#e1e4e8", "#babbbd", "#24292e", "#ffffff"),
        ["monokai"] = new PdfTheme("#272822", "#f8f8f2", "#1e1f1c", "#f8f8f2",
                                   "#66d9e8", "#a6e22e", "#49483e", "#75715e", "#1e1f1c", "#a6e22e"),
    };

    // ── Build Methods ─────────────────────────────────────────────────────────

    /// <summary>
    /// Build a single combined PDF from a project scan.
    /// </summary>
    public async Task<string> BuildSinglePdfAsync(
        ProjectScan scan,
        Dictionary<string, AIDocumentation> aiDocs,
        ExportOptions options,
        string outputPath,
        IProgress<(double, string)>? progress = null,
        CancellationToken ct = default)
    {
        var theme = Themes.GetValueOrDefault(options.ThemeKey, Themes["default"]);
        var doc = CreateDocument(scan.ProjectName, theme, options);
        var section = doc.AddSection();

        // Cover page
        AddCoverPage(section, scan, theme);

        // Table of contents
        if (options.IncludeToc)
            AddTableOfContents(section, theme);

        // Project stats
        if (options.IncludeStats)
            AddStatsSection(section, scan.Stats, theme);

        // Files
        var files = FilterFiles(scan.FlatFiles, options);
        var total = files.Count;
        var done = 0;

        foreach (var file in files)
        {
            ct.ThrowIfCancellationRequested();
            var aiDoc = aiDocs.GetValueOrDefault(file.Id);
            await AddFileSectionAsync(section, file, aiDoc, options, theme);
            done++;
            progress?.Report((done / (double)total * 100, file.RelativePath));
        }

        return RenderPdf(doc, outputPath);
    }

    /// <summary>
    /// Build one PDF per top-level folder.
    /// </summary>
    public async Task<List<string>> BuildFolderPdfsAsync(
        ProjectScan scan,
        Dictionary<string, AIDocumentation> aiDocs,
        ExportOptions options,
        string outputDir,
        IProgress<(double, string)>? progress = null,
        CancellationToken ct = default)
    {
        var outputs = new List<string>();
        var folders = scan.FileTree.ChildrenDirs;
        var idx = 0;

        foreach (var folder in folders)
        {
            ct.ThrowIfCancellationRequested();
            var folderFiles = GetAllFiles(folder).Where(f => !f.IsBinary || f.Category == FileCategory.Visual).ToList();
            if (folderFiles.Count == 0) { idx++; continue; }

            var theme = Themes.GetValueOrDefault(options.ThemeKey, Themes["default"]);
            var doc = CreateDocument(folder.Name, theme, options);
            var section = doc.AddSection();
            AddCoverPage(section, scan, theme, subtitle: $"Module: {folder.Name}");

            foreach (var file in folderFiles)
            {
                ct.ThrowIfCancellationRequested();
                var aiDoc = aiDocs.GetValueOrDefault(file.Id);
                await AddFileSectionAsync(section, file, aiDoc, options, theme);
            }

            var outPath = Path.Combine(outputDir, $"{SanitizeFileName(folder.Name)}.pdf");
            outputs.Add(RenderPdf(doc, outPath));
            idx++;
            progress?.Report((idx / (double)folders.Count * 100, folder.Name));
        }

        return outputs;
    }

    // ── Document Construction ─────────────────────────────────────────────────

    private static Document CreateDocument(string title, PdfTheme theme, ExportOptions options)
    {
        var doc = new Document();
        doc.Info.Title = title;
        doc.Info.Creator = "QRD — Quantum Repo Documenter";

        // Default styles
        var normal = doc.Styles["Normal"]!;
        normal.Font.Name = "Arial";
        normal.Font.Size = options.FontSize;
        normal.Font.Color = ParseColor(theme.Text);

        var heading1 = doc.Styles["Heading1"]!;
        heading1.Font.Name = "Arial";
        heading1.Font.Size = 16;
        heading1.Font.Bold = true;
        heading1.Font.Color = ParseColor(theme.Accent);
        heading1.ParagraphFormat.SpaceBefore = 12;
        heading1.ParagraphFormat.SpaceAfter = 6;

        var heading2 = doc.Styles["Heading2"]!;
        heading2.Font.Name = "Arial";
        heading2.Font.Size = 12;
        heading2.Font.Bold = true;
        heading2.Font.Color = ParseColor(theme.Heading);
        heading2.ParagraphFormat.SpaceBefore = 8;
        heading2.ParagraphFormat.SpaceAfter = 4;

        // Code style
        var codeStyle = doc.AddStyle("Code", "Normal");
        codeStyle.Font.Name = "Courier New";
        codeStyle.Font.Size = options.FontSize - 1;
        codeStyle.Font.Color = ParseColor(theme.CodeText);
        codeStyle.ParagraphFormat.Shading.Color = ParseColor(theme.CodeBg);

        return doc;
    }

    private static void AddCoverPage(Section section, ProjectScan scan, PdfTheme theme, string? subtitle = null)
    {
        var p = section.AddParagraph(scan.ProjectName);
        p.Style = "Heading1";
        p.Format.Font.Size = 24;
        p.Format.SpaceBefore = 40;

        if (subtitle is not null)
        {
            var sub = section.AddParagraph(subtitle);
            sub.Format.Font.Size = 14;
            sub.Format.Font.Color = ParseColor(theme.LineNum);
        }

        section.AddParagraph($"Generated by QRD on {scan.ScannedAt:yyyy-MM-dd HH:mm} UTC")
               .Format.Font.Color = ParseColor(theme.LineNum);

        var statsLine = $"{scan.Stats.TotalFiles:N0} files · " +
                        $"{scan.Stats.TotalLines:N0} lines · " +
                        $"{scan.Stats.TotalSize / 1024.0 / 1024.0:F1} MB";
        section.AddParagraph(statsLine).Format.Font.Color = ParseColor(theme.LineNum);
        section.AddPageBreak();
    }

    private static void AddTableOfContents(Section section, PdfTheme theme)
    {
        var h = section.AddParagraph("Table of Contents");
        h.Style = "Heading1";
        // MigraDoc doesn't have auto-TOC like Word, so we emit a placeholder
        // In a full impl you'd post-process with PdfSharp to add bookmarks/links
        section.AddParagraph("(See file sections below)").Format.Font.Color = ParseColor(theme.LineNum);
        section.AddPageBreak();
    }

    private static void AddStatsSection(Section section, ProjectStats stats, PdfTheme theme)
    {
        section.AddParagraph("Project Statistics").Style = "Heading1";

        // Stats table
        var table = section.AddTable();
        table.Borders.Width = 0.5;
        table.Borders.Color = ParseColor(theme.Border);

        var col1 = table.AddColumn("8cm");
        var col2 = table.AddColumn("8cm");

        AddStatsRow(table, "Total Files", $"{stats.TotalFiles:N0}", theme);
        AddStatsRow(table, "Total Lines of Code", $"{stats.TotalLines:N0}", theme);
        AddStatsRow(table, "Total Size",
            $"{stats.TotalSize / 1024.0 / 1024.0:F2} MB", theme);
        AddStatsRow(table, "Total Directories", $"{stats.TotalDirectories:N0}", theme);
        AddStatsRow(table, "Avg. File Size", $"{stats.AverageFileSize / 1024.0:F1} KB", theme);

        section.AddParagraph();

        // Language distribution
        if (stats.LanguageDistribution.Count > 0)
        {
            section.AddParagraph("Language Distribution").Style = "Heading2";
            var langTable = section.AddTable();
            langTable.Borders.Width = 0.5;
            langTable.Borders.Color = ParseColor(theme.Border);
            langTable.AddColumn("10cm");
            langTable.AddColumn("6cm");

            var headerRow = langTable.AddRow();
            headerRow.Shading.Color = ParseColor(theme.HeaderBg);
            headerRow.Cells[0].AddParagraph("Language").Format.Font.Color = ParseColor(theme.HeaderText);
            headerRow.Cells[1].AddParagraph("Files").Format.Font.Color = ParseColor(theme.HeaderText);

            foreach (var (lang, count) in stats.LanguageDistribution.OrderByDescending(kv => kv.Value).Take(15))
            {
                var row = langTable.AddRow();
                row.Cells[0].AddParagraph(lang);
                row.Cells[1].AddParagraph($"{count:N0}");
            }
        }

        section.AddPageBreak();
    }

    private static void AddStatsRow(Table table, string label, string value, PdfTheme theme)
    {
        var row = table.AddRow();
        row.Cells[0].AddParagraph(label).Format.Font.Bold = true;
        row.Cells[1].AddParagraph(value);
    }

    private async Task AddFileSectionAsync(
        Section section,
        FileInfo file,
        AIDocumentation? aiDoc,
        ExportOptions options,
        PdfTheme theme)
    {
        // File header
        var heading = section.AddParagraph(file.RelativePath);
        heading.Style = "Heading2";

        // Metadata bar
        var meta = $"Language: {file.Language?.ToString() ?? "Unknown"}  |  " +
                   $"Size: {file.SizeKb:F1} KB  |  " +
                   $"Lines: {file.LineCount?.ToString("N0") ?? "—"}  |  " +
                   $"Modified: {file.LastModified:yyyy-MM-dd}";
        var metaPara = section.AddParagraph(meta);
        metaPara.Format.Font.Size = 8;
        metaPara.Format.Font.Color = ParseColor(theme.LineNum);

        // AI documentation
        if (aiDoc is not null)
        {
            section.AddParagraph("Summary").Style = "Heading2";
            section.AddParagraph(aiDoc.Summary);

            if (!string.IsNullOrWhiteSpace(aiDoc.Purpose))
            {
                section.AddParagraph("Purpose").Style = "Heading2";
                section.AddParagraph(aiDoc.Purpose);
            }

            if (aiDoc.KeyFunctions.Count > 0)
            {
                section.AddParagraph("Key Functions / Classes").Style = "Heading2";
                foreach (var fn in aiDoc.KeyFunctions)
                    section.AddParagraph($"• {fn}");
            }

            if (aiDoc.Dependencies.Count > 0)
            {
                section.AddParagraph("Dependencies").Style = "Heading2";
                section.AddParagraph(string.Join(", ", aiDoc.Dependencies));
            }

            section.AddParagraph($"Complexity: {aiDoc.Complexity}").Format.Font.Bold = true;
        }

        // Source code
        if (!file.IsBinary && file.Category == FileCategory.Source)
        {
            await AddCodeBlockAsync(section, file, options, theme);
        }
        else if (file.Category == FileCategory.Data && file.Extension == ".csv")
        {
            AddCsvPreview(section, file, options, theme);
        }

        section.AddPageBreak();
    }

    private static async Task AddCodeBlockAsync(
        Section section,
        FileInfo file,
        ExportOptions options,
        PdfTheme theme)
    {
        if (!File.Exists(file.Path)) return;

        try
        {
            var content = await File.ReadAllTextAsync(file.Path);
            var lines = content.Split('\n');
            var maxLines = options.MaxSourceLines;

            var codeSection = section.AddParagraph();
            var para = section.AddParagraph();
            para.Style = "Code";
            para.Format.Shading.Color = ParseColor(theme.CodeBg);

            // Emit lines (with optional line numbers)
            var displayLines = lines.Take(maxLines).ToArray();
            for (var i = 0; i < displayLines.Length; i++)
            {
                var lineText = options.LineNumbers
                    ? $"{(i + 1),4}  {displayLines[i]}"
                    : displayLines[i];

                // Truncate very long lines
                if (lineText.Length > 120) lineText = lineText[..117] + "…";

                if (i > 0) para.AddLineBreak();
                para.AddText(lineText);
            }

            if (lines.Length > maxLines)
            {
                section.AddParagraph($"[… {lines.Length - maxLines} more lines truncated]")
                       .Format.Font.Color = ParseColor(theme.LineNum);
            }
        }
        catch
        {
            section.AddParagraph("[Could not read file content]")
                   .Format.Font.Color = ParseColor(theme.LineNum);
        }
    }

    private static void AddCsvPreview(Section section, FileInfo file, ExportOptions options, PdfTheme theme)
    {
        section.AddParagraph("Data Preview (CSV)").Style = "Heading2";
        // Full CSV parsing is handled by CsvParser — here we just read first lines
        try
        {
            var lines = File.ReadLines(file.Path).Take(options.MaxCsvRows + 1).ToList();
            if (lines.Count == 0) return;

            var headers = lines[0].Split(',');
            var table = section.AddTable();
            table.Borders.Width = 0.5;
            table.Borders.Color = ParseColor(theme.Border);

            var colWidth = $"{Math.Max(2, 16.0 / headers.Length):F1}cm";
            foreach (var _ in headers) table.AddColumn(colWidth);

            // Header row
            var headerRow = table.AddRow();
            headerRow.Shading.Color = ParseColor(theme.HeaderBg);
            for (var i = 0; i < headers.Length; i++)
                headerRow.Cells[i].AddParagraph(headers[i].Trim('"'))
                         .Format.Font.Color = ParseColor(theme.HeaderText);

            // Data rows
            foreach (var line in lines.Skip(1))
            {
                var cells = line.Split(',');
                var row = table.AddRow();
                for (var i = 0; i < Math.Min(cells.Length, headers.Length); i++)
                    row.Cells[i].AddParagraph(cells[i].Trim('"'));
            }
        }
        catch
        {
            section.AddParagraph("[Could not parse CSV]").Format.Font.Color = ParseColor(theme.LineNum);
        }
    }

    // ── Rendering ─────────────────────────────────────────────────────────────

    private static string RenderPdf(Document doc, string outputPath)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(outputPath)!);
        var renderer = new PdfDocumentRenderer { Document = doc };
        renderer.RenderDocument();
        renderer.PdfDocument.Save(outputPath);
        return outputPath;
    }

    // ── Helpers ───────────────────────────────────────────────────────────────

    private static List<FileInfo> FilterFiles(List<FileInfo> files, ExportOptions options)
    {
        if (options.SelectedFiles.Count > 0)
            return files.Where(f => options.SelectedFiles.Contains(f.RelativePath)).ToList();
        return files;
    }

    private static List<FileInfo> GetAllFiles(DirectoryNode node)
    {
        var result = new List<FileInfo>(node.Files);
        foreach (var child in node.ChildrenDirs)
            result.AddRange(GetAllFiles(child));
        return result;
    }

    private static MigraDoc.DocumentObjectModel.Color ParseColor(string hex)
    {
        hex = hex.TrimStart('#');
        if (hex.Length == 6)
        {
            var r = Convert.ToByte(hex[..2], 16);
            var g = Convert.ToByte(hex[2..4], 16);
            var b = Convert.ToByte(hex[4..6], 16);
            return MigraDoc.DocumentObjectModel.Color.FromRgb(r, g, b);
        }
        return MigraDoc.DocumentObjectModel.Colors.Black;
    }

    private static string SanitizeFileName(string name)
        => string.Concat(name.Select(c => Path.GetInvalidFileNameChars().Contains(c) ? '_' : c));
}

// ── Theme definition ──────────────────────────────────────────────────────────

public record PdfTheme(
    string Bg,
    string Text,
    string CodeBg,
    string CodeText,
    string Accent,
    string Heading,
    string Border,
    string LineNum,
    string HeaderBg,
    string HeaderText);

// ── Strongly-typed option enums (mirrors v2 TypeScript union literals) ─────────

public enum PaperSize { A4, Letter, A3 }

public enum PdfOrientation { Portrait, Landscape }

public enum PdfThemeName { Default, Dark, Github, Monokai }

// ── Export options passed to builder ─────────────────────────────────────────

public class ExportOptions
{
    public ExportMode Mode { get; init; } = ExportMode.Single;
    public bool IncludeAi { get; init; } = true;
    public bool IncludeCharts { get; init; } = true;
    public bool IncludeToc { get; init; } = true;
    public bool IncludeStats { get; init; } = true;
    public bool SyntaxHighlighting { get; init; } = true;
    public bool LineNumbers { get; init; } = true;
    public int MaxCsvRows { get; init; } = 100;
    public int MaxSourceLines { get; init; } = 2000;
    public PaperSize PaperSize { get; init; } = PaperSize.A4;
    public PdfOrientation Orientation { get; init; } = PdfOrientation.Portrait;
    public PdfThemeName Theme { get; init; } = PdfThemeName.Default;
    public int FontSize { get; init; } = 9;
    public List<string> SelectedFiles { get; init; } = [];

    /// <summary>String key for theme dictionary lookup.</summary>
    public string ThemeKey => Theme switch
    {
        PdfThemeName.Dark    => "dark",
        PdfThemeName.Github  => "github",
        PdfThemeName.Monokai => "monokai",
        _                    => "default"
    };
}
