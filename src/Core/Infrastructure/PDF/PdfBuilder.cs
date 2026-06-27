using MigraDoc.DocumentObjectModel;
using MigraDoc.DocumentObjectModel.Tables;
using MigraDoc.Rendering;
using QRD.Core.Domain.Entities;
using FileInfo = QRD.Core.Domain.Entities.FileInfo;

namespace QRD.Core.Infrastructure.PDF;

/// <summary>
/// PDF generation engine using MigraDoc/PdfSharp.
/// Produces professional, well-formatted documentation PDFs.
/// </summary>
public class PdfBuilder
{
    // ── Themes ───────────────────────────────────────────────────────────────

    public static readonly Dictionary<string, PdfTheme> Themes = new()
    {
        ["default"] = new PdfTheme("#ffffff", "#1a1a2e", "#f6f8fa", "#24292e",
                                   "#0969da", "#0969da", "#d0d7de", "#6e7681", "#0969da", "#ffffff",
                                   "#f0f6ff", "#dbeafe"),
        ["dark"]    = new PdfTheme("#0d1117", "#c9d1d9", "#161b22", "#c9d1d9",
                                   "#58a6ff", "#58a6ff", "#30363d", "#8b949e", "#161b22", "#58a6ff",
                                   "#1f2937", "#1e3a5f"),
        ["github"]  = new PdfTheme("#ffffff", "#24292e", "#f6f8fa", "#24292e",
                                   "#0366d6", "#24292e", "#e1e4e8", "#6a737d", "#24292e", "#ffffff",
                                   "#f1f8ff", "#dbedff"),
        ["monokai"] = new PdfTheme("#272822", "#f8f8f2", "#1e1f1c", "#f8f8f2",
                                   "#66d9e8", "#a6e22e", "#49483e", "#75715e", "#1e1f1c", "#a6e22e",
                                   "#2d2e27", "#383830"),
    };

    // ── Build Methods ─────────────────────────────────────────────────────────

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

        AddCoverPage(section, scan, theme);

        if (options.IncludeToc)
            AddTableOfContents(section, scan, theme);

        if (options.IncludeStats)
            AddStatsSection(section, scan.Stats, theme);

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

    // ── Document Setup ────────────────────────────────────────────────────────

    private static Document CreateDocument(string title, PdfTheme theme, ExportOptions options)
    {
        var doc = new Document();
        doc.Info.Title = title;
        doc.Info.Author = "QRD — Quantum Repo Documenter";

        // Page setup — tighter margins for denser content
        doc.DefaultPageSetup.TopMargin    = "1.8cm";
        doc.DefaultPageSetup.BottomMargin = "2.0cm";
        doc.DefaultPageSetup.LeftMargin   = "2.0cm";
        doc.DefaultPageSetup.RightMargin  = "2.0cm";

        // ── Normal / body ────────────────────────────────────────────────────
        var normal = doc.Styles["Normal"]!;
        normal.Font.Name = "Arial";
        normal.Font.Size = options.FontSize;
        normal.Font.Color = ParseColor(theme.Text);
        normal.ParagraphFormat.LineSpacingRule = LineSpacingRule.Multiple;
        normal.ParagraphFormat.LineSpacing = 1.15;

        // ── Heading 1 — section title (cover page, stats, etc.) ──────────────
        var h1 = doc.Styles["Heading1"]!;
        h1.Font.Name = "Arial";
        h1.Font.Size = 20;
        h1.Font.Bold = true;
        h1.Font.Color = ParseColor(theme.Accent);
        h1.ParagraphFormat.SpaceBefore = "6pt";
        h1.ParagraphFormat.SpaceAfter  = "4pt";

        // ── Heading 2 — file name headers ────────────────────────────────────
        var h2 = doc.Styles["Heading2"]!;
        h2.Font.Name = "Arial";
        h2.Font.Size = 11;
        h2.Font.Bold = true;
        h2.Font.Color = ParseColor(theme.Heading);
        h2.ParagraphFormat.SpaceBefore = "10pt";
        h2.ParagraphFormat.SpaceAfter  = "3pt";

        // ── Heading 3 — sub-section labels inside a file ─────────────────────
        var h3 = doc.AddStyle("Heading3", "Normal");
        h3.Font.Name = "Arial";
        h3.Font.Size = options.FontSize;
        h3.Font.Bold = true;
        h3.Font.Color = ParseColor(theme.Heading);
        h3.ParagraphFormat.SpaceBefore = "6pt";
        h3.ParagraphFormat.SpaceAfter  = "2pt";

        // ── Code ─────────────────────────────────────────────────────────────
        var code = doc.AddStyle("Code", "Normal");
        code.Font.Name = "Courier New";
        code.Font.Size = Math.Max(6, options.FontSize - 1);
        code.Font.Color = ParseColor(theme.CodeText);
        code.ParagraphFormat.SpaceBefore   = "2pt";
        code.ParagraphFormat.SpaceAfter    = "2pt";
        code.ParagraphFormat.LeftIndent    = "4pt";
        code.ParagraphFormat.RightIndent   = "4pt";
        code.ParagraphFormat.Shading.Color = ParseColor(theme.CodeBg);

        // ── Metadata bar (file info line) ────────────────────────────────────
        var meta = doc.AddStyle("Meta", "Normal");
        meta.Font.Name  = "Arial";
        meta.Font.Size  = 7.5;
        meta.Font.Color = ParseColor(theme.LineNum);
        meta.Font.Italic = true;
        meta.ParagraphFormat.SpaceAfter = "4pt";

        // ── Caption (truncation notice) ──────────────────────────────────────
        var caption = doc.AddStyle("Caption", "Normal");
        caption.Font.Size   = 7.5;
        caption.Font.Italic = true;
        caption.Font.Color  = ParseColor(theme.LineNum);

        return doc;
    }

    // ── Cover Page ────────────────────────────────────────────────────────────

    private static void AddCoverPage(Section section, ProjectScan scan, PdfTheme theme, string? subtitle = null)
    {
        // Push content down visually
        var spacer = section.AddParagraph();
        spacer.Format.SpaceBefore = "3cm";

        // ── Project name ──────────────────────────────────────────────────────
        var title = section.AddParagraph(scan.ProjectName);
        title.Format.Font.Name  = "Arial";
        title.Format.Font.Size  = 32;
        title.Format.Font.Bold  = true;
        title.Format.Font.Color = ParseColor(theme.Accent);
        title.Format.SpaceAfter = "0.3cm";

        if (subtitle is not null)
        {
            var sub = section.AddParagraph(subtitle);
            sub.Format.Font.Name  = "Arial";
            sub.Format.Font.Size  = 16;
            sub.Format.Font.Color = ParseColor(theme.LineNum);
            sub.Format.SpaceAfter = "0.2cm";
        }

        // ── Horizontal rule ───────────────────────────────────────────────────
        var rule = section.AddParagraph();
        rule.Format.Borders.Bottom.Width = 2;
        rule.Format.Borders.Bottom.Color = ParseColor(theme.Accent);
        rule.Format.SpaceAfter = "0.5cm";

        // ── Metadata block ────────────────────────────────────────────────────
        var generated = section.AddParagraph($"Generated by QRD on {scan.ScannedAt:yyyy-MM-dd HH:mm} UTC");
        generated.Format.Font.Name  = "Arial";
        generated.Format.Font.Size  = 10;
        generated.Format.Font.Color = ParseColor(theme.LineNum);

        // ── Stats summary pills ───────────────────────────────────────────────
        var statsLine = section.AddParagraph(
            $"{scan.Stats.TotalFiles:N0} files  ·  " +
            $"{scan.Stats.TotalLines:N0} lines  ·  " +
            $"{scan.Stats.TotalSize / 1024.0 / 1024.0:F1} MB  ·  " +
            $"{scan.Stats.TotalDirectories:N0} directories");
        statsLine.Format.Font.Name  = "Arial";
        statsLine.Format.Font.Size  = 10;
        statsLine.Format.Font.Color = ParseColor(theme.LineNum);
        statsLine.Format.SpaceAfter = "1cm";

        // ── Language breakdown on cover ───────────────────────────────────────
        if (scan.Stats.LanguageDistribution.Count > 0)
        {
            var langHeader = section.AddParagraph("Language Breakdown");
            langHeader.Format.Font.Name  = "Arial";
            langHeader.Format.Font.Size  = 11;
            langHeader.Format.Font.Bold  = true;
            langHeader.Format.Font.Color = ParseColor(theme.Text);
            langHeader.Format.SpaceAfter = "0.3cm";

            var table = section.AddTable();
            table.Borders.Width = 0;
            table.AddColumn("5cm");
            table.AddColumn("3cm");
            table.AddColumn("5cm");
            table.AddColumn("3cm");

            var sorted = scan.Stats.LanguageDistribution
                .OrderByDescending(kv => kv.Value)
                .Take(8)
                .ToList();

            for (var i = 0; i < sorted.Count; i += 2)
            {
                var row = table.AddRow();
                row.Cells[0].AddParagraph(sorted[i].Key)
                   .Format.Font.Bold = true;
                row.Cells[1].AddParagraph($"{sorted[i].Value} files");

                if (i + 1 < sorted.Count)
                {
                    row.Cells[2].AddParagraph(sorted[i + 1].Key)
                       .Format.Font.Bold = true;
                    row.Cells[3].AddParagraph($"{sorted[i + 1].Value} files");
                }
            }
        }

        section.AddPageBreak();
    }

    // ── Table of Contents ─────────────────────────────────────────────────────

    private static void AddTableOfContents(Section section, ProjectScan scan, PdfTheme theme)
    {
        var h = section.AddParagraph("Table of Contents");
        h.Style = "Heading1";
        h.Format.SpaceAfter = "0.4cm";

        // Separator
        var sep = section.AddParagraph();
        sep.Format.Borders.Bottom.Width = 0.75;
        sep.Format.Borders.Bottom.Color = ParseColor(theme.Border);
        sep.Format.SpaceAfter = "0.4cm";

        // Group files by top-level folder
        var groups = scan.FlatFiles
            .GroupBy(f =>
            {
                var parts = f.RelativePath.Split('/');
                return parts.Length > 1 ? parts[0] : "(root)";
            })
            .OrderBy(g => g.Key);

        var fileNum = 0;
        foreach (var group in groups)
        {
            // Folder heading
            var folderRow = section.AddParagraph($"📁  {group.Key}");
            folderRow.Format.Font.Name  = "Arial";
            folderRow.Format.Font.Size  = 9;
            folderRow.Format.Font.Bold  = true;
            folderRow.Format.Font.Color = ParseColor(theme.Accent);
            folderRow.Format.SpaceBefore = "6pt";
            folderRow.Format.SpaceAfter  = "2pt";
            folderRow.Format.LeftIndent  = "0cm";

            foreach (var file in group.OrderBy(f => f.RelativePath))
            {
                fileNum++;
                var ext  = file.Extension.TrimStart('.');
                var lang = file.Language?.ToString() ?? ext;
                var size = file.SizeKb >= 1 ? $"{file.SizeKb:F1} KB" : $"{file.SizeBytes} B";
                var lines = file.LineCount.HasValue ? $"  ·  {file.LineCount:N0} lines" : "";

                var entry = section.AddParagraph();
                entry.Format.Font.Name  = "Arial";
                entry.Format.Font.Size  = 8;
                entry.Format.LeftIndent = "0.8cm";
                entry.Format.SpaceAfter = "1pt";

                var nameRun = entry.AddFormattedText(file.RelativePath.Split('/').Last(), TextFormat.Bold);
                nameRun.Color = ParseColor(theme.Text);

                var infoRun = entry.AddFormattedText($"   {lang}  ·  {size}{lines}", TextFormat.NotBold);
                infoRun.Color = ParseColor(theme.LineNum);
                infoRun.Size  = 7.5;
            }
        }

        section.AddPageBreak();
    }

    // ── Statistics Section ────────────────────────────────────────────────────

    private static void AddStatsSection(Section section, ProjectStats stats, PdfTheme theme)
    {
        var h = section.AddParagraph("Project Statistics");
        h.Style = "Heading1";

        var sep = section.AddParagraph();
        sep.Format.Borders.Bottom.Width = 0.75;
        sep.Format.Borders.Bottom.Color = ParseColor(theme.Border);
        sep.Format.SpaceAfter = "0.5cm";

        // ── Summary cards (2-column table) ────────────────────────────────────
        var summaryTable = section.AddTable();
        summaryTable.Borders.Width = 0.5;
        summaryTable.Borders.Color = ParseColor(theme.Border);
        summaryTable.Rows.LeftIndent = 0;

        summaryTable.AddColumn("8cm");
        summaryTable.AddColumn("8cm");

        AddStatsRow(summaryTable, "Total Files",          $"{stats.TotalFiles:N0}",                           theme, true);
        AddStatsRow(summaryTable, "Total Lines of Code",  $"{stats.TotalLines:N0}",                           theme, false);
        AddStatsRow(summaryTable, "Total Size",           $"{stats.TotalSize / 1024.0 / 1024.0:F2} MB",       theme, true);
        AddStatsRow(summaryTable, "Total Directories",    $"{stats.TotalDirectories:N0}",                     theme, false);
        AddStatsRow(summaryTable, "Avg. File Size",       $"{stats.AverageFileSize / 1024.0:F1} KB",          theme, true);
        AddStatsRow(summaryTable, "Avg. Lines per File",  $"{stats.AverageLineCount:F0}",                     theme, false);

        section.AddParagraph().Format.SpaceAfter = "0.5cm";

        // ── Language distribution ─────────────────────────────────────────────
        if (stats.LanguageDistribution.Count > 0)
        {
            var lh = section.AddParagraph("Language Distribution");
            lh.Style = "Heading2";
            lh.Format.SpaceAfter = "0.3cm";

            var langTable = section.AddTable();
            langTable.Borders.Width = 0.5;
            langTable.Borders.Color = ParseColor(theme.Border);
            langTable.AddColumn("8cm");
            langTable.AddColumn("4cm");
            langTable.AddColumn("4cm");

            // Header
            var hdr = langTable.AddRow();
            hdr.Shading.Color = ParseColor(theme.AccentLight);
            hdr.HeadingFormat = true;
            StyleCell(hdr.Cells[0], "Language",   bold: true, color: ParseColor(theme.Accent));
            StyleCell(hdr.Cells[1], "Files",       bold: true, color: ParseColor(theme.Accent));
            StyleCell(hdr.Cells[2], "% of Total",  bold: true, color: ParseColor(theme.Accent));

            var totalFiles = stats.TotalFiles > 0 ? stats.TotalFiles : 1;
            var alternate = false;
            foreach (var (lang, count) in stats.LanguageDistribution.OrderByDescending(kv => kv.Value).Take(20))
            {
                var row = langTable.AddRow();
                if (alternate) row.Shading.Color = ParseColor(theme.RowAlt);
                row.Cells[0].AddParagraph(lang);
                row.Cells[1].AddParagraph($"{count:N0}");
                row.Cells[2].AddParagraph($"{count * 100.0 / totalFiles:F1}%");
                alternate = !alternate;
            }
        }

        section.AddPageBreak();
    }

    private static void AddStatsRow(Table table, string label, string value, PdfTheme theme, bool shaded)
    {
        var row = table.AddRow();
        if (shaded) row.Shading.Color = ParseColor(theme.RowAlt);
        row.Cells[0].AddParagraph(label).Format.Font.Bold = true;
        row.Cells[1].AddParagraph(value);
    }

    private static void StyleCell(Cell cell, string text, bool bold, Color color)
    {
        var p = cell.AddParagraph(text);
        p.Format.Font.Bold  = bold;
        p.Format.Font.Color = color;
    }

    // ── File Section ──────────────────────────────────────────────────────────

    private async Task AddFileSectionAsync(
        Section section,
        FileInfo file,
        AIDocumentation? aiDoc,
        ExportOptions options,
        PdfTheme theme)
    {
        // ── File header bar ───────────────────────────────────────────────────
        var heading = section.AddParagraph();
        heading.Format.Font.Name        = "Arial";
        heading.Format.Font.Size        = 11;
        heading.Format.Font.Bold        = true;
        heading.Format.Font.Color       = ParseColor(theme.Heading);
        heading.Format.Shading.Color    = ParseColor(theme.AccentLight);
        heading.Format.Borders.Width    = 0.5;
        heading.Format.Borders.Color    = ParseColor(theme.Border);
        heading.Format.LeftIndent       = "4pt";
        heading.Format.RightIndent      = "4pt";
        heading.Format.SpaceBefore      = "6pt";
        heading.Format.SpaceAfter       = "0pt";
        heading.AddText(file.RelativePath);

        // ── Metadata strip ────────────────────────────────────────────────────
        var metaBar = section.AddParagraph();
        metaBar.Format.Font.Name     = "Arial";
        metaBar.Format.Font.Size     = 7.5;
        metaBar.Format.Font.Color    = ParseColor(theme.LineNum);
        metaBar.Format.Font.Italic   = true;
        metaBar.Format.Shading.Color = ParseColor(theme.CodeBg);
        metaBar.Format.Borders.Width = 0.5;
        metaBar.Format.Borders.Color = ParseColor(theme.Border);
        metaBar.Format.LeftIndent    = "4pt";
        metaBar.Format.SpaceAfter    = "4pt";

        var lang  = file.Language?.ToString() ?? "Unknown";
        var lines = file.LineCount.HasValue ? $"{file.LineCount:N0} lines" : "—";
        metaBar.AddText($"{lang}   ·   {file.SizeKb:F1} KB   ·   {lines}   ·   {file.LastModified:yyyy-MM-dd}");

        // ── AI documentation ──────────────────────────────────────────────────
        if (aiDoc is not null)
        {
            if (!string.IsNullOrWhiteSpace(aiDoc.Summary))
            {
                var sumLabel = section.AddParagraph("Summary");
                sumLabel.Style = "Heading3";

                var sumPara = section.AddParagraph(aiDoc.Summary);
                sumPara.Format.LeftIndent  = "0.4cm";
                sumPara.Format.SpaceAfter  = "3pt";
            }

            if (!string.IsNullOrWhiteSpace(aiDoc.Purpose))
            {
                var purposeLabel = section.AddParagraph("Purpose");
                purposeLabel.Style = "Heading3";

                var purposePara = section.AddParagraph(aiDoc.Purpose);
                purposePara.Format.LeftIndent = "0.4cm";
                purposePara.Format.SpaceAfter = "3pt";
            }

            if (aiDoc.KeyFunctions.Count > 0)
            {
                var fnLabel = section.AddParagraph("Key Functions / Classes");
                fnLabel.Style = "Heading3";

                foreach (var fn in aiDoc.KeyFunctions)
                {
                    var fnPara = section.AddParagraph($"▸  {fn}");
                    fnPara.Format.Font.Size   = 8;
                    fnPara.Format.LeftIndent  = "0.6cm";
                    fnPara.Format.SpaceAfter  = "1pt";
                }
            }

            if (aiDoc.Dependencies.Count > 0)
            {
                var depLabel = section.AddParagraph("Dependencies");
                depLabel.Style = "Heading3";

                var depPara = section.AddParagraph(string.Join("  ·  ", aiDoc.Dependencies));
                depPara.Format.Font.Size  = 8;
                depPara.Format.LeftIndent = "0.4cm";
                depPara.Format.SpaceAfter = "3pt";
            }

            if (!string.IsNullOrWhiteSpace(aiDoc.Complexity))
            {
                var cxPara = section.AddParagraph($"Complexity:  {aiDoc.Complexity}");
                cxPara.Format.Font.Size  = 8;
                cxPara.Format.Font.Bold  = true;
                cxPara.Format.SpaceAfter = "4pt";
            }

            // Light separator before code
            var aiSep = section.AddParagraph();
            aiSep.Format.Borders.Bottom.Width = 0.5;
            aiSep.Format.Borders.Bottom.Color = ParseColor(theme.Border);
            aiSep.Format.SpaceAfter = "3pt";
        }

        // ── Source code ───────────────────────────────────────────────────────
        if (!file.IsBinary && file.Category == FileCategory.Source)
        {
            await AddCodeBlockAsync(section, file, options, theme);
        }
        else if (file.Category == FileCategory.Data && file.Extension == ".csv")
        {
            AddCsvPreview(section, file, options, theme);
        }
        else if (file.IsBinary)
        {
            var binaryNote = section.AddParagraph("[ Binary file — content not shown ]");
            binaryNote.Style = "Caption";
            binaryNote.Format.LeftIndent = "0.4cm";
        }

        // ── Trailing separator ────────────────────────────────────────────────
        section.AddPageBreak();
    }

    // ── Code Block ────────────────────────────────────────────────────────────

    private static async Task AddCodeBlockAsync(
        Section section,
        FileInfo file,
        ExportOptions options,
        PdfTheme theme)
    {
        if (!File.Exists(file.Path)) return;

        try
        {
            var content  = await File.ReadAllTextAsync(file.Path);
            var lines    = content.Split('\n');
            var maxLines = options.MaxSourceLines;

            // Split code into chunks of 80 lines to avoid MigraDoc single-paragraph
            // rendering issues that cause background shading to disappear
            const int chunkSize = 80;
            var displayLines = lines.Take(maxLines).ToArray();

            for (var chunkStart = 0; chunkStart < displayLines.Length; chunkStart += chunkSize)
            {
                var chunk = displayLines.Skip(chunkStart).Take(chunkSize).ToArray();
                var para  = section.AddParagraph();
                para.Style = "Code";
                para.Format.Shading.Color = ParseColor(theme.CodeBg);

                // Left border accent on code blocks
                para.Format.Borders.Left.Width = 3;
                para.Format.Borders.Left.Color = ParseColor(theme.Accent);

                for (var i = 0; i < chunk.Length; i++)
                {
                    var lineNum  = chunkStart + i + 1;
                    var lineText = chunk[i].Replace("\r", "").Replace("\t", "    ");

                    // Truncate very long lines
                    if (lineText.Length > 120) lineText = lineText[..117] + "…";

                    if (i > 0) para.AddLineBreak();

                    if (options.LineNumbers)
                    {
                        var numFmt = para.AddFormattedText($"{lineNum,4}  ", TextFormat.NotBold);
                        numFmt.Color = ParseColor(theme.LineNum);
                        numFmt.Size  = Math.Max(6, options.FontSize - 1.5);
                    }

                    para.AddText(lineText);
                }
            }

            if (lines.Length > maxLines)
            {
                var truncNote = section.AddParagraph(
                    $"[ … {lines.Length - maxLines:N0} more lines not shown. Increase MaxSourceLines in export options to include them. ]");
                truncNote.Style = "Caption";
                truncNote.Format.LeftIndent = "0.4cm";
            }
        }
        catch
        {
            var err = section.AddParagraph("[ Could not read file content ]");
            err.Style = "Caption";
        }
    }

    // ── CSV Preview ───────────────────────────────────────────────────────────

    private static void AddCsvPreview(Section section, FileInfo file, ExportOptions options, PdfTheme theme)
    {
        var h = section.AddParagraph("Data Preview (CSV)");
        h.Style = "Heading3";
        h.Format.SpaceAfter = "0.2cm";

        try
        {
            var lines = File.ReadLines(file.Path).Take(options.MaxCsvRows + 1).ToList();
            if (lines.Count == 0) return;

            var headers = lines[0].Split(',');
            var table   = section.AddTable();
            table.Borders.Width = 0.5;
            table.Borders.Color = ParseColor(theme.Border);

            var colWidth = $"{Math.Max(2, 16.0 / headers.Length):F1}cm";
            foreach (var _ in headers) table.AddColumn(colWidth);

            // Header row
            var headerRow = table.AddRow();
            headerRow.Shading.Color = ParseColor(theme.AccentLight);
            headerRow.HeadingFormat = true;
            for (var i = 0; i < headers.Length; i++)
            {
                var p = headerRow.Cells[i].AddParagraph(headers[i].Trim('"'));
                p.Format.Font.Bold  = true;
                p.Format.Font.Color = ParseColor(theme.Accent);
                p.Format.Font.Size  = 8;
            }

            // Data rows with alternating shading
            var alt = false;
            foreach (var line in lines.Skip(1))
            {
                var cells = line.Split(',');
                var row   = table.AddRow();
                if (alt) row.Shading.Color = ParseColor(theme.RowAlt);
                for (var i = 0; i < Math.Min(cells.Length, headers.Length); i++)
                {
                    var p = row.Cells[i].AddParagraph(cells[i].Trim('"'));
                    p.Format.Font.Size = 8;
                }
                alt = !alt;
            }
        }
        catch
        {
            section.AddParagraph("[ Could not parse CSV ]").Style = "Caption";
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

// ── Theme ─────────────────────────────────────────────────────────────────────

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
    string HeaderText,
    string AccentLight,   // light tinted bg for header rows / cover blocks
    string RowAlt);       // alternating row background

// ── Enums ─────────────────────────────────────────────────────────────────────

public enum PaperSize      { A4, Letter, A3 }
public enum PdfOrientation { Portrait, Landscape }
public enum PdfThemeName   { Default, Dark, Github, Monokai }

// ── ExportOptions ─────────────────────────────────────────────────────────────

public class ExportOptions
{
    public ExportMode    Mode              { get; init; } = ExportMode.Single;
    public bool          IncludeAi         { get; init; } = true;
    public bool          IncludeCharts     { get; init; } = true;
    public bool          IncludeToc        { get; init; } = true;
    public bool          IncludeStats      { get; init; } = true;
    public bool          SyntaxHighlighting{ get; init; } = true;
    public bool          LineNumbers       { get; init; } = true;
    public int           MaxCsvRows        { get; init; } = 100;
    public int           MaxSourceLines    { get; init; } = 2000;
    public PaperSize     PaperSize         { get; init; } = PaperSize.A4;
    public PdfOrientation Orientation      { get; init; } = PdfOrientation.Portrait;
    public PdfThemeName  Theme             { get; init; } = PdfThemeName.Default;
    public int           FontSize          { get; init; } = 9;
    public List<string>  SelectedFiles     { get; init; } = [];

    public string ThemeKey => Theme switch
    {
        PdfThemeName.Dark    => "dark",
        PdfThemeName.Github  => "github",
        PdfThemeName.Monokai => "monokai",
        _                    => "default"
    };
}
