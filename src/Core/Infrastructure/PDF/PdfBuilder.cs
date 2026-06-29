using System.Text.RegularExpressions;
using MigraDoc.DocumentObjectModel;
using MigraDoc.DocumentObjectModel.Tables;
using MigraDoc.Rendering;
using QRD.Core.Domain.Entities;
using FileInfo = QRD.Core.Domain.Entities.FileInfo;

namespace QRD.Core.Infrastructure.PDF;

/// <summary>
/// PDF generation engine using MigraDoc/PdfSharp.
///
/// Document order:
///   1. Cover page  — project name, LOC stats, language breakdown
///   2. README      — rendered verbatim if found (root files first, then sub-folders)
///   3. TOC         — file list grouped by folder
///   4. File tree   — ASCII directory tree
///   5. Stats page  — tables + language distribution
///   6. Files       — root files first (alphabetical), then sub-folders alphabetically
///                    Each file: shaded header, metadata strip, syntax-highlighted code
/// </summary>
public class PdfBuilder
{
    // ── Themes ────────────────────────────────────────────────────────────────

    public static readonly Dictionary<string, PdfTheme> Themes = new()
    {
        ["default"] = new PdfTheme(
            Bg:          "#ffffff", Text:       "#1a1a2e",
            CodeBg:      "#f6f8fa", CodeText:   "#24292e",
            Accent:      "#0969da", Heading:    "#0969da",
            Border:      "#d0d7de", LineNum:    "#6e7681",
            HeaderBg:    "#0969da", HeaderText: "#ffffff",
            AccentLight: "#f0f6ff", RowAlt:     "#f6f8fa",
            // Syntax colours (default = GitHub light)
            Kw:    "#cf222e", Str:   "#0a3069", Num:   "#0550ae",
            Cmt:   "#6e7681", Type:  "#953800", Fn:    "#8250df",
            Op:    "#0969da", Prep:  "#0969da"),

        ["dark"] = new PdfTheme(
            Bg:          "#0d1117", Text:       "#c9d1d9",
            CodeBg:      "#161b22", CodeText:   "#c9d1d9",
            Accent:      "#58a6ff", Heading:    "#58a6ff",
            Border:      "#30363d", LineNum:    "#8b949e",
            HeaderBg:    "#161b22", HeaderText: "#58a6ff",
            AccentLight: "#1f2937", RowAlt:     "#161b22",
            Kw:    "#ff7b72", Str:   "#a5d6ff", Num:   "#79c0ff",
            Cmt:   "#8b949e", Type:  "#ffa657", Fn:    "#d2a8ff",
            Op:    "#58a6ff", Prep:  "#58a6ff"),

        ["github"] = new PdfTheme(
            Bg:          "#ffffff", Text:       "#24292e",
            CodeBg:      "#f6f8fa", CodeText:   "#24292e",
            Accent:      "#0366d6", Heading:    "#24292e",
            Border:      "#e1e4e8", LineNum:    "#6a737d",
            HeaderBg:    "#24292e", HeaderText: "#ffffff",
            AccentLight: "#f1f8ff", RowAlt:     "#f6f8fa",
            Kw:    "#d73a49", Str:   "#032f62", Num:   "#005cc5",
            Cmt:   "#6a737d", Type:  "#e36209", Fn:    "#6f42c1",
            Op:    "#0366d6", Prep:  "#0366d6"),

        ["monokai"] = new PdfTheme(
            Bg:          "#272822", Text:       "#f8f8f2",
            CodeBg:      "#1e1f1c", CodeText:   "#f8f8f2",
            Accent:      "#66d9e8", Heading:    "#a6e22e",
            Border:      "#49483e", LineNum:    "#75715e",
            HeaderBg:    "#1e1f1c", HeaderText: "#a6e22e",
            AccentLight: "#2d2e27", RowAlt:     "#272822",
            Kw:    "#f92672", Str:   "#e6db74", Num:   "#ae81ff",
            Cmt:   "#75715e", Type:  "#66d9e8", Fn:    "#a6e22e",
            Op:    "#f92672", Prep:  "#66d9e8"),
    };

    // ── Public build entry points ─────────────────────────────────────────────

    public async Task<string> BuildSinglePdfAsync(
        ProjectScan scan,
        Dictionary<string, AIDocumentation> aiDocs,
        ExportOptions options,
        string outputPath,
        IProgress<(double, string)>? progress = null,
        CancellationToken ct = default)
    {
        var theme = Themes.GetValueOrDefault(options.ThemeKey, Themes["default"]);
        var doc   = CreateDocument(scan.ProjectName, theme, options);
        var sec   = doc.AddSection();

        // 1 — Cover
        AddCoverPage(sec, scan, theme);

        // 2 — README (if present)
        var readmeFile = scan.FlatFiles.FirstOrDefault(f =>
            Path.GetFileName(f.RelativePath).Equals("README.md", StringComparison.OrdinalIgnoreCase) ||
            Path.GetFileName(f.RelativePath).Equals("README.txt", StringComparison.OrdinalIgnoreCase) ||
            Path.GetFileName(f.RelativePath).Equals("README",     StringComparison.OrdinalIgnoreCase));

        if (readmeFile is not null)
            await AddReadmeSectionAsync(sec, readmeFile, theme);

        // 3 — TOC
        if (options.IncludeToc)
            AddTableOfContents(sec, scan, theme);

        // 4 — File tree
        AddFileTreeSection(sec, scan, theme);

        // 5 — Stats
        if (options.IncludeStats)
            AddStatsSection(sec, scan.Stats, theme);

        // 6 — Files: root first, then sub-folders alphabetically
        var orderedFiles = OrderedFiles(FilterFiles(scan.FlatFiles, options));
        var total = orderedFiles.Count;
        var done  = 0;

        foreach (var file in orderedFiles)
        {
            ct.ThrowIfCancellationRequested();
            var aiDoc = aiDocs.GetValueOrDefault(file.Id);
            await AddFileSectionAsync(sec, file, aiDoc, options, theme);
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

        foreach (var folder in folders.OrderBy(f => f.Name))
        {
            ct.ThrowIfCancellationRequested();
            var folderFiles = GetAllFiles(folder)
                .Where(f => !f.IsBinary || f.Category == FileCategory.Visual)
                .ToList();
            if (folderFiles.Count == 0) { idx++; continue; }

            var theme = Themes.GetValueOrDefault(options.ThemeKey, Themes["default"]);
            var doc   = CreateDocument(folder.Name, theme, options);
            var sec   = doc.AddSection();
            AddCoverPage(sec, scan, theme, subtitle: $"Module: {folder.Name}");
            AddFileTreeSection(sec, scan, theme, rootNode: folder);

            foreach (var file in OrderedFiles(folderFiles))
            {
                ct.ThrowIfCancellationRequested();
                var aiDoc = aiDocs.GetValueOrDefault(file.Id);
                await AddFileSectionAsync(sec, file, aiDoc, options, theme);
            }

            var outPath = Path.Combine(outputDir, $"{SanitizeFileName(folder.Name)}.pdf");
            outputs.Add(RenderPdf(doc, outPath));
            idx++;
            progress?.Report((idx / (double)folders.Count * 100, folder.Name));
        }

        return outputs;
    }

    // ── Document styles ───────────────────────────────────────────────────────

    private static Document CreateDocument(string title, PdfTheme theme, ExportOptions options)
    {
        var doc = new Document();
        doc.Info.Title  = title;
        doc.Info.Author = "QRD — Quantum Repo Documenter";

        doc.DefaultPageSetup.TopMargin    = "1.8cm";
        doc.DefaultPageSetup.BottomMargin = "2.0cm";
        doc.DefaultPageSetup.LeftMargin   = "2.0cm";
        doc.DefaultPageSetup.RightMargin  = "2.0cm";

        var normal = doc.Styles["Normal"]!;
        normal.Font.Name  = "Arial";
        normal.Font.Size  = options.FontSize;
        normal.Font.Color = ParseColor(theme.Text);
        normal.ParagraphFormat.LineSpacingRule = LineSpacingRule.Multiple;
        normal.ParagraphFormat.LineSpacing     = 1.15;

        var h1 = doc.Styles["Heading1"]!;
        h1.Font.Name  = "Arial"; h1.Font.Size = 20; h1.Font.Bold = true;
        h1.Font.Color = ParseColor(theme.Accent);
        h1.ParagraphFormat.SpaceBefore = "6pt"; h1.ParagraphFormat.SpaceAfter = "4pt";

        var h2 = doc.Styles["Heading2"]!;
        h2.Font.Name  = "Arial"; h2.Font.Size = 13; h2.Font.Bold = true;
        h2.Font.Color = ParseColor(theme.Heading);
        h2.ParagraphFormat.SpaceBefore = "8pt"; h2.ParagraphFormat.SpaceAfter = "3pt";

        var h3 = doc.AddStyle("Heading3", "Normal");
        h3.Font.Name  = "Arial"; h3.Font.Size = options.FontSize; h3.Font.Bold = true;
        h3.Font.Color = ParseColor(theme.Heading);
        h3.ParagraphFormat.SpaceBefore = "5pt"; h3.ParagraphFormat.SpaceAfter = "2pt";

        var code = doc.AddStyle("Code", "Normal");
        code.Font.Name  = "Courier New";
        code.Font.Size  = Math.Max(6, options.FontSize - 1);
        code.Font.Color = ParseColor(theme.CodeText);
        code.ParagraphFormat.SpaceBefore   = "1pt"; code.ParagraphFormat.SpaceAfter = "1pt";
        code.ParagraphFormat.LeftIndent    = "4pt"; code.ParagraphFormat.RightIndent = "4pt";
        code.ParagraphFormat.Shading.Color = ParseColor(theme.CodeBg);

        var tree = doc.AddStyle("Tree", "Normal");
        tree.Font.Name  = "Courier New"; tree.Font.Size = 8;
        tree.Font.Color = ParseColor(theme.CodeText);
        tree.ParagraphFormat.SpaceAfter    = "1pt";
        tree.ParagraphFormat.LeftIndent    = "4pt";
        tree.ParagraphFormat.Shading.Color = ParseColor(theme.CodeBg);

        var caption = doc.AddStyle("Caption", "Normal");
        caption.Font.Size = 7.5; caption.Font.Italic = true;
        caption.Font.Color = ParseColor(theme.LineNum);

        return doc;
    }

    // ── 1. Cover page ─────────────────────────────────────────────────────────

    private static void AddCoverPage(Section sec, ProjectScan scan, PdfTheme theme, string? subtitle = null)
    {
        var sp = sec.AddParagraph(); sp.Format.SpaceBefore = "3cm";

        var title = sec.AddParagraph(scan.ProjectName);
        title.Format.Font.Name = "Arial"; title.Format.Font.Size = 32;
        title.Format.Font.Bold = true; title.Format.Font.Color = ParseColor(theme.Accent);
        title.Format.SpaceAfter = "0.3cm";

        if (subtitle is not null)
        {
            var sub = sec.AddParagraph(subtitle);
            sub.Format.Font.Name  = "Arial"; sub.Format.Font.Size  = 16;
            sub.Format.Font.Color = ParseColor(theme.LineNum); sub.Format.SpaceAfter = "0.2cm";
        }

        // Thick rule
        var rule = sec.AddParagraph();
        rule.Format.Borders.Bottom.Width = 2.5;
        rule.Format.Borders.Bottom.Color = ParseColor(theme.Accent);
        rule.Format.SpaceAfter = "0.6cm";

        // Generated line
        var gen = sec.AddParagraph($"Generated by QRD  ·  {scan.ScannedAt:yyyy-MM-dd HH:mm} UTC");
        gen.Format.Font.Name  = "Arial"; gen.Format.Font.Size  = 10;
        gen.Format.Font.Color = ParseColor(theme.LineNum); gen.Format.SpaceAfter = "0.2cm";

        // Stats summary
        var stats = sec.AddParagraph(
            $"{scan.Stats.TotalFiles:N0} files  ·  " +
            $"{scan.Stats.TotalLines:N0} lines of code  ·  " +
            $"{scan.Stats.TotalSize / 1_048_576.0:F1} MB  ·  " +
            $"{scan.Stats.TotalDirectories:N0} directories");
        stats.Format.Font.Name  = "Arial"; stats.Format.Font.Size  = 11;
        stats.Format.Font.Bold  = true;   stats.Format.Font.Color = ParseColor(theme.Text);
        stats.Format.SpaceAfter = "1cm";

        // Language breakdown — 2-column table
        var sorted = scan.Stats.LanguageDistribution
            .OrderByDescending(kv => kv.Value).Take(10).ToList();

        if (sorted.Count > 0)
        {
            var lh = sec.AddParagraph("Languages");
            lh.Format.Font.Name  = "Arial"; lh.Format.Font.Size = 11;
            lh.Format.Font.Bold  = true;    lh.Format.Font.Color = ParseColor(theme.Text);
            lh.Format.SpaceAfter = "0.3cm";

            var tbl = sec.AddTable();
            tbl.Borders.Width = 0;
            tbl.AddColumn("5.5cm"); tbl.AddColumn("3cm");
            tbl.AddColumn("5.5cm"); tbl.AddColumn("3cm");

            var total = scan.Stats.TotalFiles > 0 ? scan.Stats.TotalFiles : 1;
            for (var i = 0; i < sorted.Count; i += 2)
            {
                var row = tbl.AddRow();
                row.Cells[0].AddParagraph(sorted[i].Key).Format.Font.Bold = true;
                row.Cells[1].AddParagraph($"{sorted[i].Value} files  ({sorted[i].Value * 100 / total}%)");
                if (i + 1 < sorted.Count)
                {
                    row.Cells[2].AddParagraph(sorted[i + 1].Key).Format.Font.Bold = true;
                    row.Cells[3].AddParagraph($"{sorted[i + 1].Value} files  ({sorted[i + 1].Value * 100 / total}%)");
                }
            }
        }

        sec.AddPageBreak();
    }

    // ── 2. README section ─────────────────────────────────────────────────────

    private static async Task AddReadmeSectionAsync(Section sec, FileInfo readmeFile, PdfTheme theme)
    {
        if (!File.Exists(readmeFile.Path)) return;

        var h = sec.AddParagraph("README");
        h.Style = "Heading1"; h.Format.SpaceAfter = "0.3cm";

        var sep = sec.AddParagraph();
        sep.Format.Borders.Bottom.Width = 0.75;
        sep.Format.Borders.Bottom.Color = ParseColor(theme.Border);
        sep.Format.SpaceAfter = "0.4cm";

        try
        {
            var text  = await File.ReadAllTextAsync(readmeFile.Path);
            var lines = text.Split('\n');

            foreach (var rawLine in lines)
            {
                var line = rawLine.TrimEnd('\r');

                if (line.StartsWith("# "))
                {
                    var p = sec.AddParagraph(line[2..]);
                    p.Format.Font.Name = "Arial"; p.Format.Font.Size = 16;
                    p.Format.Font.Bold = true;    p.Format.Font.Color = ParseColor(theme.Accent);
                    p.Format.SpaceBefore = "6pt"; p.Format.SpaceAfter = "3pt";
                }
                else if (line.StartsWith("## "))
                {
                    var p = sec.AddParagraph(line[3..]);
                    p.Format.Font.Name = "Arial"; p.Format.Font.Size = 13;
                    p.Format.Font.Bold = true;    p.Format.Font.Color = ParseColor(theme.Heading);
                    p.Format.SpaceBefore = "5pt"; p.Format.SpaceAfter = "2pt";
                }
                else if (line.StartsWith("### "))
                {
                    var p = sec.AddParagraph(line[4..]);
                    p.Format.Font.Name = "Arial"; p.Format.Font.Size = 11;
                    p.Format.Font.Bold = true;    p.Format.Font.Color = ParseColor(theme.Heading);
                    p.Format.SpaceBefore = "4pt"; p.Format.SpaceAfter = "2pt";
                }
                else if (line.StartsWith("    ") || line.StartsWith("\t"))
                {
                    var p = sec.AddParagraph(line.TrimStart('\t').TrimStart(' ', ' ', ' ', ' '));
                    p.Style = "Code"; p.Format.Shading.Color = ParseColor(theme.CodeBg);
                    p.Format.Borders.Left.Width = 2; p.Format.Borders.Left.Color = ParseColor(theme.Accent);
                }
                else if (line.StartsWith("- ") || line.StartsWith("* ") || line.StartsWith("+ "))
                {
                    var p = sec.AddParagraph($"  •  {line[2..]}");
                    p.Format.LeftIndent = "0.4cm";
                }
                else if (string.IsNullOrWhiteSpace(line))
                {
                    var p = sec.AddParagraph(); p.Format.SpaceAfter = "3pt";
                }
                else
                {
                    sec.AddParagraph(line);
                }
            }
        }
        catch
        {
            sec.AddParagraph("[ Could not read README ]").Style = "Caption";
        }

        sec.AddPageBreak();
    }

    // ── 3. Table of Contents ─────────────────────────────────────────────────

    private static void AddTableOfContents(Section sec, ProjectScan scan, PdfTheme theme)
    {
        var h = sec.AddParagraph("Table of Contents");
        h.Style = "Heading1"; h.Format.SpaceAfter = "0.4cm";

        var sep = sec.AddParagraph();
        sep.Format.Borders.Bottom.Width = 0.75;
        sep.Format.Borders.Bottom.Color = ParseColor(theme.Border);
        sep.Format.SpaceAfter = "0.5cm";

        var ordered = OrderedFiles(scan.FlatFiles);

        // Root-level files first
        var rootFiles = ordered.Where(f => !f.RelativePath.Contains('/')).ToList();
        if (rootFiles.Count > 0)
        {
            var rh = sec.AddParagraph("/  (root)");
            rh.Format.Font.Name = "Arial"; rh.Format.Font.Size = 9;
            rh.Format.Font.Bold = true;    rh.Format.Font.Color = ParseColor(theme.Accent);
            rh.Format.SpaceAfter = "2pt";
            foreach (var f in rootFiles) AddTocEntry(sec, f, theme, indent: "0.6cm");
        }

        // Sub-folders alphabetically
        var folderGroups = ordered
            .Where(f => f.RelativePath.Contains('/'))
            .GroupBy(f => f.RelativePath.Split('/')[0])
            .OrderBy(g => g.Key);

        foreach (var grp in folderGroups)
        {
            var fh = sec.AddParagraph($"  {grp.Key}/");
            fh.Format.Font.Name  = "Arial"; fh.Format.Font.Size = 9;
            fh.Format.Font.Bold  = true;    fh.Format.Font.Color = ParseColor(theme.Accent);
            fh.Format.SpaceBefore = "5pt"; fh.Format.SpaceAfter = "2pt";
            foreach (var f in grp) AddTocEntry(sec, f, theme, indent: "1.2cm");
        }

        sec.AddPageBreak();
    }

    private static void AddTocEntry(Section sec, FileInfo file, PdfTheme theme, string indent)
    {
        var entry = sec.AddParagraph();
        entry.Format.Font.Name  = "Arial"; entry.Format.Font.Size = 8;
        entry.Format.LeftIndent = indent;  entry.Format.SpaceAfter = "1pt";

        var name = entry.AddFormattedText(file.RelativePath.Split('/').Last(), TextFormat.Bold);
        name.Color = ParseColor(theme.Text);

        var lang  = file.Language?.ToString() ?? file.Extension.TrimStart('.');
        var size  = file.SizeKb >= 1 ? $"{file.SizeKb:F1} KB" : $"{file.SizeBytes} B";
        var lines = file.LineCount.HasValue ? $"  ·  {file.LineCount:N0} lines" : "";

        var info = entry.AddFormattedText($"   {lang}  ·  {size}{lines}", TextFormat.NotBold);
        info.Color = ParseColor(theme.LineNum); info.Size = 7.5;
    }

    // ── 4. File tree ─────────────────────────────────────────────────────────

    private static void AddFileTreeSection(Section sec, ProjectScan scan, PdfTheme theme,
        DirectoryNode? rootNode = null)
    {
        var h = sec.AddParagraph("File Tree");
        h.Style = "Heading1"; h.Format.SpaceAfter = "0.3cm";

        var sep = sec.AddParagraph();
        sep.Format.Borders.Bottom.Width = 0.75;
        sep.Format.Borders.Bottom.Color = ParseColor(theme.Border);
        sep.Format.SpaceAfter = "0.4cm";

        var node  = rootNode ?? scan.FileTree;
        var lines = new List<string>();
        BuildTreeLines(node, "", true, lines);

        // Render as one big code-style paragraph per chunk of 60 lines
        const int chunk = 60;
        for (var start = 0; start < lines.Count; start += chunk)
        {
            var para = sec.AddParagraph();
            para.Style = "Tree";
            para.Format.Shading.Color = ParseColor(theme.CodeBg);
            para.Format.Borders.Left.Width = 2;
            para.Format.Borders.Left.Color = ParseColor(theme.Border);

            var slice = lines.Skip(start).Take(chunk).ToList();
            for (var i = 0; i < slice.Count; i++)
            {
                if (i > 0) para.AddLineBreak();
                para.AddText(slice[i]);
            }
        }

        sec.AddPageBreak();
    }

    private static void BuildTreeLines(DirectoryNode node, string prefix, bool isLast,
        List<string> lines, int depth = 0)
    {
        // Root node — just emit the folder name
        if (depth == 0)
        {
            lines.Add($"{node.Name}/");
        }
        else
        {
            var connector = isLast ? "└── " : "├── ";
            lines.Add($"{prefix}{connector}{node.Name}/");
        }

        var childPrefix = depth == 0 ? "" : prefix + (isLast ? "    " : "│   ");

        // Sort: sub-folders first, then files, both alphabetically
        var subDirs = node.ChildrenDirs.OrderBy(d => d.Name).ToList();
        var files   = node.Files.OrderBy(f => f.Name).ToList();

        for (var i = 0; i < subDirs.Count; i++)
            BuildTreeLines(subDirs[i], childPrefix, i == subDirs.Count - 1 && files.Count == 0, lines, depth + 1);

        for (var i = 0; i < files.Count; i++)
        {
            var connector = (i == files.Count - 1) ? "└── " : "├── ";
            var lang      = files[i].Language?.ToString() ?? files[i].Extension.TrimStart('.');
            var size      = files[i].SizeKb >= 1 ? $"{files[i].SizeKb:F0} KB" : $"{files[i].SizeBytes} B";
            lines.Add($"{childPrefix}{connector}{files[i].Name}  [{lang}  {size}]");
        }
    }

    // ── 5. Stats ──────────────────────────────────────────────────────────────

    private static void AddStatsSection(Section sec, ProjectStats stats, PdfTheme theme)
    {
        var h = sec.AddParagraph("Project Statistics");
        h.Style = "Heading1";

        var sep = sec.AddParagraph();
        sep.Format.Borders.Bottom.Width = 0.75;
        sep.Format.Borders.Bottom.Color = ParseColor(theme.Border);
        sep.Format.SpaceAfter = "0.5cm";

        var tbl = sec.AddTable();
        tbl.Borders.Width = 0.5; tbl.Borders.Color = ParseColor(theme.Border);
        tbl.AddColumn("8cm"); tbl.AddColumn("8cm");

        AddStatsRow(tbl, "Total Files",         $"{stats.TotalFiles:N0}",                         theme, true);
        AddStatsRow(tbl, "Total Lines of Code", $"{stats.TotalLines:N0}",                         theme, false);
        AddStatsRow(tbl, "Total Size",          $"{stats.TotalSize / 1_048_576.0:F2} MB",         theme, true);
        AddStatsRow(tbl, "Total Directories",   $"{stats.TotalDirectories:N0}",                   theme, false);
        AddStatsRow(tbl, "Avg. File Size",      $"{stats.AverageFileSize / 1024.0:F1} KB",        theme, true);
        AddStatsRow(tbl, "Avg. Lines/File",     $"{stats.AverageLineCount:F0}",                   theme, false);

        sec.AddParagraph().Format.SpaceAfter = "0.5cm";

        if (stats.LanguageDistribution.Count > 0)
        {
            var lh = sec.AddParagraph("Language Distribution");
            lh.Style = "Heading2"; lh.Format.SpaceAfter = "0.3cm";

            var lt = sec.AddTable();
            lt.Borders.Width = 0.5; lt.Borders.Color = ParseColor(theme.Border);
            lt.AddColumn("8cm"); lt.AddColumn("4cm"); lt.AddColumn("4cm");

            var hdr = lt.AddRow();
            hdr.Shading.Color  = ParseColor(theme.AccentLight);
            hdr.HeadingFormat  = true;
            StyleCell(hdr.Cells[0], "Language",  bold: true, color: ParseColor(theme.Accent));
            StyleCell(hdr.Cells[1], "Files",     bold: true, color: ParseColor(theme.Accent));
            StyleCell(hdr.Cells[2], "% of Total",bold: true, color: ParseColor(theme.Accent));

            var total = stats.TotalFiles > 0 ? stats.TotalFiles : 1;
            var alt   = false;
            foreach (var (lang, count) in stats.LanguageDistribution.OrderByDescending(kv => kv.Value).Take(20))
            {
                var row = lt.AddRow();
                if (alt) row.Shading.Color = ParseColor(theme.RowAlt);
                row.Cells[0].AddParagraph(lang);
                row.Cells[1].AddParagraph($"{count:N0}");
                row.Cells[2].AddParagraph($"{count * 100.0 / total:F1}%");
                alt = !alt;
            }
        }

        sec.AddPageBreak();
    }

    private static void AddStatsRow(Table t, string label, string value, PdfTheme theme, bool shaded)
    {
        var row = t.AddRow();
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

    // ── 6. File section ───────────────────────────────────────────────────────

    private async Task AddFileSectionAsync(
        Section sec, FileInfo file, AIDocumentation? aiDoc,
        ExportOptions options, PdfTheme theme)
    {
        // Header bar
        var hdr = sec.AddParagraph();
        hdr.Format.Font.Name     = "Arial"; hdr.Format.Font.Size  = 11;
        hdr.Format.Font.Bold     = true;    hdr.Format.Font.Color = ParseColor(theme.Heading);
        hdr.Format.Shading.Color = ParseColor(theme.AccentLight);
        hdr.Format.Borders.Width = 0.5;     hdr.Format.Borders.Color = ParseColor(theme.Border);
        hdr.Format.LeftIndent    = "4pt";   hdr.Format.SpaceBefore = "8pt";
        hdr.Format.SpaceAfter    = "0pt";
        hdr.AddText(file.RelativePath);

        // Metadata strip
        var meta = sec.AddParagraph();
        meta.Format.Font.Name     = "Arial"; meta.Format.Font.Size   = 7.5;
        meta.Format.Font.Italic   = true;    meta.Format.Font.Color  = ParseColor(theme.LineNum);
        meta.Format.Shading.Color = ParseColor(theme.CodeBg);
        meta.Format.Borders.Width = 0.5;     meta.Format.Borders.Color = ParseColor(theme.Border);
        meta.Format.LeftIndent    = "4pt";   meta.Format.SpaceAfter  = "5pt";
        var lang  = file.Language?.ToString() ?? "Unknown";
        var locs  = file.LineCount.HasValue ? $"{file.LineCount:N0} lines" : "—";
        meta.AddText($"{lang}   ·   {file.SizeKb:F1} KB   ·   {locs}   ·   {file.LastModified:yyyy-MM-dd}");

        // AI docs
        if (aiDoc is not null)
        {
            if (!string.IsNullOrWhiteSpace(aiDoc.Summary))
            {
                sec.AddParagraph("Summary").Style = "Heading3";
                var p = sec.AddParagraph(aiDoc.Summary);
                p.Format.LeftIndent = "0.4cm"; p.Format.SpaceAfter = "3pt";
            }
            if (!string.IsNullOrWhiteSpace(aiDoc.Purpose))
            {
                sec.AddParagraph("Purpose").Style = "Heading3";
                var p = sec.AddParagraph(aiDoc.Purpose);
                p.Format.LeftIndent = "0.4cm"; p.Format.SpaceAfter = "3pt";
            }
            if (aiDoc.KeyFunctions.Count > 0)
            {
                sec.AddParagraph("Key Functions / Classes").Style = "Heading3";
                foreach (var fn in aiDoc.KeyFunctions)
                {
                    var p = sec.AddParagraph($"  ▸  {fn}");
                    p.Format.Font.Size = 8; p.Format.LeftIndent = "0.6cm"; p.Format.SpaceAfter = "1pt";
                }
            }
            if (aiDoc.Dependencies.Count > 0)
            {
                sec.AddParagraph("Dependencies").Style = "Heading3";
                var p = sec.AddParagraph(string.Join("  ·  ", aiDoc.Dependencies));
                p.Format.Font.Size = 8; p.Format.LeftIndent = "0.4cm"; p.Format.SpaceAfter = "3pt";
            }
            if (!string.IsNullOrWhiteSpace(aiDoc.Complexity))
            {
                var p = sec.AddParagraph($"Complexity:  {aiDoc.Complexity}");
                p.Format.Font.Size = 8; p.Format.Font.Bold = true; p.Format.SpaceAfter = "4pt";
            }
            var aiSep = sec.AddParagraph();
            aiSep.Format.Borders.Bottom.Width = 0.5;
            aiSep.Format.Borders.Bottom.Color = ParseColor(theme.Border);
            aiSep.Format.SpaceAfter = "3pt";
        }

        // Content
        if (!file.IsBinary && file.Category == FileCategory.Source)
            await AddCodeBlockAsync(sec, file, options, theme);
        else if (file.Category == FileCategory.Data && file.Extension == ".csv")
            AddCsvPreview(sec, file, options, theme);
        else if (file.IsBinary)
        {
            var p = sec.AddParagraph("[ Binary file — content not shown ]");
            p.Style = "Caption"; p.Format.LeftIndent = "0.4cm";
        }

        sec.AddPageBreak();
    }

    // ── Syntax-highlighted code block ─────────────────────────────────────────

    private static async Task AddCodeBlockAsync(
        Section sec, FileInfo file, ExportOptions options, PdfTheme theme)
    {
        if (!File.Exists(file.Path)) return;

        try
        {
            var content      = await File.ReadAllTextAsync(file.Path);
            var allLines     = content.Split('\n');
            var maxLines     = options.MaxSourceLines;
            var displayLines = allLines.Take(maxLines).ToArray();
            var highlighter  = GetHighlighter(file.Language, file.Extension);
            const int chunk  = 60;

            for (var start = 0; start < displayLines.Length; start += chunk)
            {
                var slice = displayLines.Skip(start).Take(chunk).ToArray();
                var para  = sec.AddParagraph();
                para.Style = "Code";
                para.Format.Shading.Color          = ParseColor(theme.CodeBg);
                para.Format.Borders.Left.Width     = 3;
                para.Format.Borders.Left.Color     = ParseColor(theme.Accent);

                for (var i = 0; i < slice.Length; i++)
                {
                    if (i > 0) para.AddLineBreak();

                    if (options.LineNumbers)
                    {
                        var num = para.AddFormattedText($"{start + i + 1,4}  ", TextFormat.NotBold);
                        num.Color = ParseColor(theme.LineNum);
                        num.Size  = Math.Max(6, options.FontSize - 1.5);
                    }

                    var lineText = slice[i].Replace("\r", "").Replace("\t", "    ");
                    if (lineText.Length > 130) lineText = lineText[..127] + "…";

                    if (options.SyntaxHighlighting && highlighter is not null)
                        EmitHighlightedLine(para, lineText, highlighter, theme);
                    else
                        para.AddText(lineText);
                }
            }

            if (allLines.Length > maxLines)
            {
                var note = sec.AddParagraph(
                    $"[ … {allLines.Length - maxLines:N0} more lines not shown ]");
                note.Style = "Caption"; note.Format.LeftIndent = "0.4cm";
            }
        }
        catch
        {
            sec.AddParagraph("[ Could not read file content ]").Style = "Caption";
        }
    }

    // ── Syntax highlighting engine ────────────────────────────────────────────

    private static void EmitHighlightedLine(
        Paragraph para, string line, SyntaxHighlighter hl, PdfTheme theme)
    {
        if (string.IsNullOrEmpty(line)) { para.AddText(""); return; }

        var tokens = hl.Tokenize(line);
        foreach (var (text, kind) in tokens)
        {
            if (string.IsNullOrEmpty(text)) continue;
            var run   = para.AddFormattedText(text, TextFormat.NotBold);
            run.Color = kind switch
            {
                TokenKind.Keyword     => ParseColor(theme.Kw),
                TokenKind.String      => ParseColor(theme.Str),
                TokenKind.Number      => ParseColor(theme.Num),
                TokenKind.Comment     => ParseColor(theme.Cmt),
                TokenKind.Type        => ParseColor(theme.Type),
                TokenKind.Function    => ParseColor(theme.Fn),
                TokenKind.Operator    => ParseColor(theme.Op),
                TokenKind.Preprocessor=> ParseColor(theme.Prep),
                _                     => ParseColor(theme.CodeText)
            };
            if (kind == TokenKind.Comment) run.Italic = true;
        }
    }

    private static SyntaxHighlighter? GetHighlighter(Language? lang, string ext) => lang switch
    {
        Language.CSharp     => new CSharpHighlighter(),
        Language.Python     => new PythonHighlighter(),
        Language.JavaScript => new JsHighlighter(),
        Language.TypeScript => new JsHighlighter(),
        Language.ReactTsx   => new JsHighlighter(),
        Language.Sql        => new SqlHighlighter(),
        Language.Yaml       => new YamlHighlighter(),
        Language.Json       => new JsonHighlighter(),
        Language.Xml        => new XmlHighlighter(),
        Language.Html       => new XmlHighlighter(),
        Language.Shell      => new ShellHighlighter(),
        Language.Bash       => new ShellHighlighter(),
        Language.Css        => new CssHighlighter(),
        Language.Java       => new CSharpHighlighter(), // similar enough
        Language.Go         => new CSharpHighlighter(),
        Language.Rust       => new CSharpHighlighter(),
        Language.Cpp        => new CSharpHighlighter(),
        _                   => null
    };

    // ── CSV preview ───────────────────────────────────────────────────────────

    private static void AddCsvPreview(Section sec, FileInfo file, ExportOptions options, PdfTheme theme)
    {
        sec.AddParagraph("Data Preview (CSV)").Style = "Heading3";
        try
        {
            var lines = File.ReadLines(file.Path).Take(options.MaxCsvRows + 1).ToList();
            if (lines.Count == 0) return;
            var headers  = lines[0].Split(',');
            var colWidth = $"{Math.Max(2, 16.0 / headers.Length):F1}cm";
            var tbl      = sec.AddTable();
            tbl.Borders.Width = 0.5; tbl.Borders.Color = ParseColor(theme.Border);
            foreach (var _ in headers) tbl.AddColumn(colWidth);
            var hdr = tbl.AddRow();
            hdr.Shading.Color = ParseColor(theme.AccentLight); hdr.HeadingFormat = true;
            for (var i = 0; i < headers.Length; i++)
            {
                var p = hdr.Cells[i].AddParagraph(headers[i].Trim('"'));
                p.Format.Font.Bold = true; p.Format.Font.Color = ParseColor(theme.Accent);
                p.Format.Font.Size = 8;
            }
            var alt = false;
            foreach (var line in lines.Skip(1))
            {
                var cells = line.Split(',');
                var row   = tbl.AddRow();
                if (alt) row.Shading.Color = ParseColor(theme.RowAlt);
                for (var i = 0; i < Math.Min(cells.Length, headers.Length); i++)
                    tbl.Rows[tbl.Rows.Count - 1].Cells[i].AddParagraph(cells[i].Trim('"'))
                       .Format.Font.Size = 8;
                alt = !alt;
            }
        }
        catch { sec.AddParagraph("[ Could not parse CSV ]").Style = "Caption"; }
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

    /// Root files first (alphabetical), then sub-folders alphabetically (recursively)
    private static List<FileInfo> OrderedFiles(IEnumerable<FileInfo> files)
    {
        var all    = files.ToList();
        var root   = all.Where(f => !f.RelativePath.Contains('/')).OrderBy(f => f.Name).ToList();
        var others = all.Where(f =>  f.RelativePath.Contains('/'))
                        .OrderBy(f => f.RelativePath)
                        .ToList();
        root.AddRange(others);
        return root;
    }

    private static List<FileInfo> FilterFiles(List<FileInfo> files, ExportOptions options)
        => options.SelectedFiles.Count > 0
            ? files.Where(f => options.SelectedFiles.Contains(f.RelativePath)).ToList()
            : files;

    private static List<FileInfo> GetAllFiles(DirectoryNode node)
    {
        var r = new List<FileInfo>(node.Files);
        foreach (var c in node.ChildrenDirs) r.AddRange(GetAllFiles(c));
        return r;
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
    string Bg, string Text, string CodeBg, string CodeText,
    string Accent, string Heading, string Border, string LineNum,
    string HeaderBg, string HeaderText, string AccentLight, string RowAlt,
    // Syntax highlight colours
    string Kw, string Str, string Num, string Cmt,
    string Type, string Fn, string Op, string Prep);

// ── Syntax highlighting ───────────────────────────────────────────────────────

public enum TokenKind { Normal, Keyword, String, Number, Comment, Type, Function, Operator, Preprocessor }

public abstract class SyntaxHighlighter
{
    public abstract List<(string Text, TokenKind Kind)> Tokenize(string line);

    protected static List<(string, TokenKind)> Single(string text, TokenKind kind)
        => [(text, kind)];

    /// Simple greedy tokenizer helper: split line into tokens by regex patterns.
    protected static List<(string, TokenKind)> Lex(string line,
        (System.Text.RegularExpressions.Regex Pattern, TokenKind Kind)[] rules)
    {
        var result  = new List<(string, TokenKind)>();
        var pos     = 0;
        while (pos < line.Length)
        {
            (string, TokenKind)? best = null;
            var bestIdx = int.MaxValue;
            System.Text.RegularExpressions.Match? bestMatch = null;

            foreach (var (pat, kind) in rules)
            {
                var m = pat.Match(line, pos);
                if (m.Success && m.Index < bestIdx)
                {
                    bestIdx   = m.Index;
                    best      = (m.Value, kind);
                    bestMatch = m;
                }
            }

            if (best is null || bestIdx == int.MaxValue)
            {
                result.Add((line[pos..], TokenKind.Normal));
                break;
            }

            if (bestIdx > pos)
                result.Add((line[pos..bestIdx], TokenKind.Normal));

            result.Add(best.Value);
            pos = bestIdx + bestMatch!.Length;
        }
        return result;
    }
}

public class CSharpHighlighter : SyntaxHighlighter
{
    // Keywords — uses \b word boundary, verbatim string, no quote issues
    private static readonly Regex KwPat = new Regex(
        @"\b(abstract|as|base|bool|break|byte|case|catch|char|checked|class|const|continue|" +
        @"decimal|default|delegate|do|double|else|enum|event|explicit|extern|false|finally|" +
        @"fixed|float|for|foreach|goto|if|implicit|in|int|interface|internal|is|lock|long|" +
        @"namespace|new|null|object|operator|out|override|params|private|protected|public|" +
        @"readonly|ref|return|sbyte|sealed|short|sizeof|stackalloc|static|string|struct|" +
        @"switch|this|throw|true|try|typeof|uint|ulong|unchecked|unsafe|ushort|using|" +
        @"virtual|void|volatile|while|async|await|var|dynamic|record|init|required|" +
        @"file|scoped|nint|nuint)\b");
    private static readonly Regex CmtPat  = new Regex(@"//.*$");
    private static readonly Regex StrPat  = new Regex(@"@""[^""]*""|""(?:[^""\\]|\\.)*""");
    private static readonly Regex CharPat = new Regex(@"'(?:[^'\\]|\\.)*'");
    private static readonly Regex NumPat  = new Regex(@"\b\d+(\.\d+)?([eE][+-]?\d+)?[fFdDmMlLuU]?\b");
    private static readonly Regex TypPat  = new Regex(@"\b[A-Z][A-Za-z0-9_]*\b");
    private static readonly Regex FnPat   = new Regex(@"\b[A-Z][A-Za-z0-9_]*(?=\s*[({<])");
    private static readonly Regex OpPat   = new Regex(@"[+\-*/%&|^~<>=!?:]+");
    private static readonly Regex PrePat  = new Regex(@"#\w+");

    public override List<(string, TokenKind)> Tokenize(string line)
        => Lex(line, new[] {
            (CmtPat, TokenKind.Comment), (StrPat, TokenKind.String),
            (CharPat, TokenKind.String), (KwPat, TokenKind.Keyword),
            (FnPat, TokenKind.Function), (TypPat, TokenKind.Type),
            (NumPat, TokenKind.Number),  (OpPat, TokenKind.Operator),
            (PrePat, TokenKind.Preprocessor) });
}

public class PythonHighlighter : SyntaxHighlighter
{
    private static readonly Regex CmtPat = new Regex(@"#.*$");
    private static readonly Regex StrPat = new Regex("\"[^\"\\n]*\"|'[^'\\n]*'");
    private static readonly Regex KwPat  = new Regex(
        @"\b(False|None|True|and|as|assert|async|await|break|class|continue|def|del|elif|" +
        @"else|except|finally|for|from|global|if|import|in|is|lambda|nonlocal|not|or|pass|" +
        @"raise|return|try|while|with|yield)\b");
    private static readonly Regex FnPat  = new Regex(@"(?<=def\s)[A-Za-z_]\w*");
    private static readonly Regex TypPat = new Regex(@"\b[A-Z][A-Za-z0-9_]*\b");
    private static readonly Regex NumPat = new Regex(@"\b\d+(\.\d+)?([eEjJ])?\b");
    private static readonly Regex DecPat = new Regex(@"@[A-Za-z_]\w*");
    private static readonly Regex OpPat  = new Regex(@"[+\-*/%&|^~<>=!]+");

    public override List<(string, TokenKind)> Tokenize(string line)
        => Lex(line, new[] {
            (CmtPat, TokenKind.Comment), (StrPat, TokenKind.String),
            (KwPat, TokenKind.Keyword),  (FnPat, TokenKind.Function),
            (TypPat, TokenKind.Type),    (NumPat, TokenKind.Number),
            (DecPat, TokenKind.Preprocessor), (OpPat, TokenKind.Operator) });
}

public class JsHighlighter : SyntaxHighlighter
{
    private static readonly Regex CmtPat = new Regex(@"//.*$");
    private static readonly Regex StrPat = new Regex(@"`[^`\n]*`|""[^""\n]*""|'[^'\n]*'");
    private static readonly Regex KwPat  = new Regex(
        @"\b(async|await|break|case|catch|class|const|continue|debugger|default|delete|do|" +
        @"else|export|extends|finally|for|from|function|if|import|in|instanceof|let|new|" +
        @"null|of|return|static|super|switch|this|throw|true|false|try|typeof|undefined|" +
        @"var|void|while|with|yield|type|interface|enum|implements|declare|abstract|" +
        @"readonly|override)\b");
    private static readonly Regex TypPat = new Regex(@"\b[A-Z][A-Za-z0-9_]*\b");
    private static readonly Regex NumPat = new Regex(@"\b\d+(\.\d+)?(n)?\b");
    private static readonly Regex OpPat  = new Regex(@"[+\-*/%&|^~<>=!?:]+");

    public override List<(string, TokenKind)> Tokenize(string line)
        => Lex(line, new[] {
            (CmtPat, TokenKind.Comment), (StrPat, TokenKind.String),
            (KwPat, TokenKind.Keyword),  (TypPat, TokenKind.Type),
            (NumPat, TokenKind.Number),  (OpPat, TokenKind.Operator) });
}

public class SqlHighlighter : SyntaxHighlighter
{
    private static readonly Regex CmtPat = new Regex(@"--.*$");
    private static readonly Regex StrPat = new Regex(@"'[^'\n]*'");
    private static readonly Regex KwPat  = new Regex(
        @"\b(ADD|ALL|ALTER|AND|AS|ASC|BETWEEN|BY|CASE|COLUMN|CONSTRAINT|CREATE|DATABASE|" +
        @"DEFAULT|DELETE|DESC|DISTINCT|DROP|ELSE|END|EXISTS|FOREIGN|FROM|FULL|GROUP|HAVING|" +
        @"IN|INDEX|INNER|INSERT|INTO|IS|JOIN|KEY|LEFT|LIKE|LIMIT|NOT|NULL|ON|OR|ORDER|OUTER|" +
        @"PRIMARY|REFERENCES|RIGHT|SELECT|SET|TABLE|THEN|TOP|TRUNCATE|UNION|UNIQUE|UPDATE|" +
        @"VALUES|VIEW|WHERE|WITH)\b", RegexOptions.IgnoreCase);
    private static readonly Regex NumPat = new Regex(@"\b\d+(\.\d+)?\b");

    public override List<(string, TokenKind)> Tokenize(string line)
        => Lex(line, new[] {
            (CmtPat, TokenKind.Comment), (StrPat, TokenKind.String),
            (KwPat, TokenKind.Keyword),  (NumPat, TokenKind.Number) });
}

public class YamlHighlighter : SyntaxHighlighter
{
    private static readonly Regex CmtPat = new Regex(@"#.*$");
    private static readonly Regex StrPat = new Regex("\"[^\"\\n]*\"|'[^'\\n]*'");
    private static readonly Regex KeyPat = new Regex(@"^\s*[\w\-]+\s*:");
    private static readonly Regex BolPat = new Regex(@"\b(true|false|null|yes|no)\b", RegexOptions.IgnoreCase);
    private static readonly Regex NumPat = new Regex(@"\b\d+(\.\d+)?\b");
    private static readonly Regex DocPat = new Regex(@"^---$|^\.\.\.$");

    public override List<(string, TokenKind)> Tokenize(string line)
        => Lex(line, new[] {
            (CmtPat, TokenKind.Comment), (StrPat, TokenKind.String),
            (KeyPat, TokenKind.Keyword), (BolPat, TokenKind.Type),
            (NumPat, TokenKind.Number),  (DocPat, TokenKind.Preprocessor) });
}

public class JsonHighlighter : SyntaxHighlighter
{
    private static readonly Regex KeyPat = new Regex("\"[^\"\\n]*\"\\s*:");
    private static readonly Regex StrPat = new Regex("\"[^\"\\n]*\"");
    private static readonly Regex BolPat = new Regex(@"\b(true|false|null)\b");
    private static readonly Regex NumPat = new Regex(@"-?\d+(\.\d+)?([eE][+-]?\d+)?\b");

    public override List<(string, TokenKind)> Tokenize(string line)
        => Lex(line, new[] {
            (KeyPat, TokenKind.Keyword), (StrPat, TokenKind.String),
            (BolPat, TokenKind.Type),    (NumPat, TokenKind.Number) });
}

public class XmlHighlighter : SyntaxHighlighter
{
    private static readonly Regex CmtPat = new Regex(@"<!--.*?-->");
    private static readonly Regex TagPat = new Regex(@"<[!/]?[\w:-]+");
    private static readonly Regex StrPat = new Regex("\"[^\"\\n]*\"");
    private static readonly Regex AttPat = new Regex(@"[\w:-]+=");
    private static readonly Regex ClsPat = new Regex(@"/?>|</[\w:-]+>");

    public override List<(string, TokenKind)> Tokenize(string line)
        => Lex(line, new[] {
            (CmtPat, TokenKind.Comment), (TagPat, TokenKind.Keyword),
            (StrPat, TokenKind.String),  (AttPat, TokenKind.Type),
            (ClsPat, TokenKind.Keyword) });
}

public class ShellHighlighter : SyntaxHighlighter
{
    private static readonly Regex ShnPat = new Regex(@"^#!.*$");
    private static readonly Regex CmtPat = new Regex(@"(?<!#!)#.*$");
    private static readonly Regex StrPat = new Regex("\"[^\"\\n]*\"|'[^'\\n]*'");
    private static readonly Regex KwPat  = new Regex(
        @"\b(if|then|else|elif|fi|for|in|do|done|while|until|case|esac|function|return|exit|" +
        @"export|source|echo|local|readonly|declare|set|unset|shift|exec|eval|trap|continue|break)\b");
    private static readonly Regex VarPat = new Regex(@"\$\{?[\w#@*?!-]+\}?");
    private static readonly Regex NumPat = new Regex(@"\b\d+\b");

    public override List<(string, TokenKind)> Tokenize(string line)
        => Lex(line, new[] {
            (ShnPat, TokenKind.Preprocessor), (CmtPat, TokenKind.Comment),
            (StrPat, TokenKind.String),        (KwPat, TokenKind.Keyword),
            (VarPat, TokenKind.Type),          (NumPat, TokenKind.Number) });
}

public class CssHighlighter : SyntaxHighlighter
{
    private static readonly Regex CmtPat = new Regex(@"/\*.*?\*/");
    private static readonly Regex StrPat = new Regex("\"[^\"\\n]*\"|'[^'\\n]*'");
    private static readonly Regex PropPat= new Regex(@"[\w-]+\s*:");
    private static readonly Regex HexPat = new Regex(@"#[0-9a-fA-F]{3,8}\b");
    private static readonly Regex NumPat = new Regex(@"-?\d+(\.\d+)?(px|em|rem|%|vh|vw|pt|cm|mm|s|ms|fr|deg)?");
    private static readonly Regex AtPat  = new Regex(@"@[\w-]+");
    private static readonly Regex SelPat = new Regex(@"[.#:[\]~>+*]");

    public override List<(string, TokenKind)> Tokenize(string line)
        => Lex(line, new[] {
            (CmtPat, TokenKind.Comment), (StrPat, TokenKind.String),
            (PropPat, TokenKind.Keyword),(HexPat, TokenKind.Number),
            (NumPat, TokenKind.Number),  (AtPat, TokenKind.Preprocessor),
            (SelPat, TokenKind.Operator) });
}

public enum PaperSize      { A4, Letter, A3 }
public enum PdfOrientation { Portrait, Landscape }
public enum PdfThemeName   { Default, Dark, Github, Monokai }

// ── ExportOptions ─────────────────────────────────────────────────────────────

public class ExportOptions
{
    public ExportMode     Mode               { get; init; } = ExportMode.Single;
    public bool           IncludeAi          { get; init; } = true;
    public bool           IncludeCharts      { get; init; } = true;
    public bool           IncludeToc         { get; init; } = true;
    public bool           IncludeStats       { get; init; } = true;
    public bool           SyntaxHighlighting { get; init; } = true;
    public bool           LineNumbers        { get; init; } = true;
    public int            MaxCsvRows         { get; init; } = 100;
    public int            MaxSourceLines     { get; init; } = 2000;
    public PaperSize      PaperSize          { get; init; } = PaperSize.A4;
    public PdfOrientation Orientation        { get; init; } = PdfOrientation.Portrait;
    public PdfThemeName   Theme              { get; init; } = PdfThemeName.Default;
    public int            FontSize           { get; init; } = 9;
    public List<string>   SelectedFiles      { get; init; } = [];
    public string         CoverNote          { get; init; } = "";

    public string ThemeKey => Theme switch
    {
        PdfThemeName.Dark    => "dark",
        PdfThemeName.Github  => "github",
        PdfThemeName.Monokai => "monokai",
        _                    => "default"
    };
}
