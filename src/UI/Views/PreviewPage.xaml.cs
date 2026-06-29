using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Media;
using QRD.Core.Domain.Entities;
using QRD.Core.Infrastructure.PDF;
using QRD.Core.Infrastructure.Syntax;
using QRD.Utils;
using MessageBox   = System.Windows.MessageBox;
using Color        = System.Windows.Media.Color;
using Orientation  = System.Windows.Controls.Orientation;

namespace QRD.UI.Views;

/// <summary>
/// Live preview page — renders a FlowDocument that mirrors the PDF layout
/// so the user can tweak formatting before committing to export.
/// </summary>
public partial class PreviewPage : Page
{
    private readonly HttpClient              _http;
    private readonly AppSettings             _settings;
    private readonly LanguageExtensionManager _langMgr;

    private CancellationTokenSource? _previewCts;
    private JsonElement?              _lastScan;

    public PreviewPage(AppSettings settings, LanguageExtensionManager langMgr)
    {
        InitializeComponent();
        _settings = settings;
        _langMgr  = langMgr;
        _http     = new HttpClient { BaseAddress = new Uri($"http://{settings.Host}:{settings.Port}") };
    }

    // ── Option change handlers ─────────────────────────────────────────────

    private void Option_Changed(object sender, RoutedEventArgs e) => _ = RebuildPreviewAsync();
    private void Option_Changed(object sender, SelectionChangedEventArgs e) => _ = RebuildPreviewAsync();
    private void Option_Changed(object sender, System.Windows.Controls.TextChangedEventArgs e) => _ = RebuildPreviewAsync();

    private void FontSlider_Changed(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (FontSizeLabel is not null) FontSizeLabel.Text = ((int)e.NewValue).ToString();
        _ = RebuildPreviewAsync();
    }

    private void MaxLines_Changed(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (MaxLinesLabel is not null) MaxLinesLabel.Text = ((int)e.NewValue).ToString();
        _ = RebuildPreviewAsync();
    }

    private void BrowseProject_Click(object sender, RoutedEventArgs e)
    {
        using var dlg = new System.Windows.Forms.FolderBrowserDialog
            { Description = "Select project folder", UseDescriptionForTitle = true };
        if (dlg.ShowDialog() == System.Windows.Forms.DialogResult.OK)
            ProjectPathBox.Text = dlg.SelectedPath;
    }

    private void Refresh_Click(object sender, RoutedEventArgs e) => _ = ScanAndPreviewAsync();
    private void Export_Click(object sender, RoutedEventArgs e)  => _ = ExportAsync();

    // ── Scan → preview pipeline ────────────────────────────────────────────

    private async Task ScanAndPreviewAsync()
    {
        var path = ProjectPathBox.Text.Trim();
        if (string.IsNullOrEmpty(path))
        {
            MessageBox.Show("Please select a project folder first.", "QRD Preview");
            return;
        }

        SetStatus("Scanning project…", busy: true);
        try
        {
            var body = JsonSerializer.Serialize(new { path, exclude_patterns = Array.Empty<string>() });
            var resp = await _http.PostAsync("/scanner/scan",
                new StringContent(body, Encoding.UTF8, "application/json"));
            resp.EnsureSuccessStatusCode();
            _lastScan = JsonDocument.Parse(await resp.Content.ReadAsStringAsync()).RootElement;
            await RebuildPreviewAsync();
        }
        catch (Exception ex)
        {
            SetStatus($"Scan failed: {ex.Message}", busy: false);
        }
    }

    private async Task RebuildPreviewAsync()
    {
        if (_lastScan is null) return;

        _previewCts?.Cancel();
        _previewCts = new CancellationTokenSource();
        var ct = _previewCts.Token;

        SetStatus("Building preview…", busy: true);
        try
        {
            var opts    = BuildOptions();
            var theme   = PdfBuilder.Themes.GetValueOrDefault(opts.ThemeKey, PdfBuilder.Themes["default"]);
            var doc     = await Task.Run(() => BuildFlowDocument(_lastScan!.Value, opts, theme, ct), ct);

            if (ct.IsCancellationRequested) return;

            PreviewDoc.Blocks.Clear();
            foreach (var block in doc.Blocks.ToList()) { doc.Blocks.Remove(block); PreviewDoc.Blocks.Add(block); }

            // Theme background
            PreviewGrid.Background = new SolidColorBrush(ParseWpfColor(theme.Bg));
            PreviewViewer.Background = PreviewGrid.Background;

            PlaceholderPanel.Visibility = Visibility.Collapsed;
            PreviewViewer.Visibility    = Visibility.Visible;

            var fileCount = _lastScan!.Value.TryGetProperty("stats", out var st)
                ? (st.TryGetProperty("total_files", out var tf) ? tf.GetInt64() : 0)
                : 0L;
            SetStatus($"Preview ready  ·  {fileCount} files", busy: false);
        }
        catch (OperationCanceledException) { /* superseded */ }
        catch (Exception ex) { SetStatus($"Preview error: {ex.Message}", busy: false); }
    }

    // ── FlowDocument builder ───────────────────────────────────────────────

    private FlowDocument BuildFlowDocument(JsonElement scan, ExportOptions opts,
        PdfTheme theme, CancellationToken ct)
    {
        var doc = new FlowDocument
        {
            PageWidth   = 794,
            ColumnWidth = 794,
            FontFamily  = new FontFamily("Arial"),
            FontSize    = opts.FontSize + 1,
            Foreground  = new SolidColorBrush(ParseWpfColor(theme.Text)),
            Background  = new SolidColorBrush(ParseWpfColor(theme.Bg)),
        };

        // 1 — Cover
        AddFdCoverPage(doc, scan, theme, opts);

        // 2 — TOC
        if (opts.IncludeToc) AddFdToc(doc, scan, theme);

        // 3 — Files (root first, then sub-folders alphabetically)
        var files = GetOrderedFiles(scan);
        foreach (var file in files)
        {
            ct.ThrowIfCancellationRequested();
            AddFdFileSection(doc, file, opts, theme);
        }

        return doc;
    }

    // ── Cover page ──────────────────────────────────────────────────────────

    private static void AddFdCoverPage(FlowDocument doc, JsonElement scan, PdfTheme theme, ExportOptions opts)
    {
        var projName = scan.TryGetProperty("project_name", out var pn) ? pn.GetString() ?? "Project" : "Project";

        doc.Blocks.Add(new Paragraph(new Run(projName))
        {
            FontSize   = 32, FontWeight = FontWeights.Bold,
            Foreground = B(theme.Accent), Margin = new Thickness(0, 32, 0, 4)
        });

        // Rule
        doc.Blocks.Add(new BlockUIContainer(new System.Windows.Shapes.Rectangle
        {
            Height = 3, Fill = B(theme.Accent).Brush, HorizontalAlignment = HorizontalAlignment.Stretch
        }) { Margin = new Thickness(0, 0, 0, 12) });

        if (scan.TryGetProperty("stats", out var stats))
        {
            var files = stats.TryGetProperty("total_files", out var tf) ? tf.GetInt64() : 0;
            var lines = stats.TryGetProperty("total_lines", out var tl) ? tl.GetInt64() : 0;
            var size  = stats.TryGetProperty("total_size",  out var ts) ? ts.GetInt64() / 1_048_576.0 : 0;
            var dirs  = stats.TryGetProperty("total_directories", out var td) ? td.GetInt64() : 0;

            doc.Blocks.Add(new Paragraph(new Run(
                $"{files:N0} files  ·  {lines:N0} lines of code  ·  {size:F1} MB  ·  {dirs:N0} directories"))
            {
                FontSize = 12, FontWeight = FontWeights.SemiBold,
                Foreground = B(theme.Text).Brush, Margin = new Thickness(0, 0, 0, 8)
            });

            // Language list
            if (stats.TryGetProperty("language_distribution", out var langs))
            {
                var p = new Paragraph { Margin = new Thickness(0, 0, 0, 4) };
                foreach (var lang in langs.EnumerateObject().OrderByDescending(x => x.Value.GetInt32()).Take(8))
                {
                    p.Inlines.Add(new Run($"  {lang.Name}  ") { Foreground = B(theme.Accent).Brush, FontWeight = FontWeights.Bold });
                    p.Inlines.Add(new Run($"{lang.Value.GetInt32()} files    ") { Foreground = B(theme.LineNum).Brush, FontSize = 10 });
                }
                doc.Blocks.Add(p);
            }
        }

        if (!string.IsNullOrWhiteSpace(opts.CoverNote))
        {
            doc.Blocks.Add(new Paragraph(new Run(opts.CoverNote))
                { FontStyle = FontStyles.Italic, Foreground = B(theme.LineNum).Brush, Margin = new Thickness(0, 8, 0, 0) });
        }

        doc.Blocks.Add(new Paragraph { Margin = new Thickness(0, 32, 0, 0) }); // spacer
    }

    // ── TOC ─────────────────────────────────────────────────────────────────

    private static void AddFdToc(FlowDocument doc, JsonElement scan, PdfTheme theme)
    {
        doc.Blocks.Add(new Paragraph(new Run("Table of Contents"))
            { FontSize = 18, FontWeight = FontWeights.Bold, Foreground = B(theme.Accent).Brush, Margin = new Thickness(0, 16, 0, 4) });

        doc.Blocks.Add(new BlockUIContainer(new System.Windows.Shapes.Rectangle
            { Height = 1, Fill = B(theme.Border).Brush }) { Margin = new Thickness(0, 0, 0, 8) });

        var files = GetOrderedFiles(scan);
        string? currentFolder = null;
        foreach (var f in files)
        {
            var rel  = f.TryGetProperty("relative_path", out var rp) ? rp.GetString() ?? "" : "";
            var name = f.TryGetProperty("name", out var nm) ? nm.GetString() ?? "" : rel;
            var folder = rel.Contains('/') ? rel.Split('/')[0] : "(root)";
            var lang   = f.TryGetProperty("language", out var lg) && lg.ValueKind != JsonValueKind.Null ? lg.GetString() ?? "" : "";
            var size   = f.TryGetProperty("size",     out var sz) ? sz.GetInt64() / 1024.0 : 0;
            var lines  = f.TryGetProperty("line_count", out var lc) && lc.ValueKind != JsonValueKind.Null ? lc.GetInt32() : -1;

            if (folder != currentFolder)
            {
                currentFolder = folder;
                doc.Blocks.Add(new Paragraph(new Run($"▸  {folder}/"))
                {
                    FontWeight = FontWeights.Bold, FontSize = 10,
                    Foreground = B(theme.Accent).Brush,
                    Margin     = new Thickness(0, 6, 0, 2)
                });
            }

            var entry = new Paragraph { Margin = new Thickness(16, 0, 0, 1) };
            entry.Inlines.Add(new Run(name) { FontWeight = FontWeights.SemiBold, Foreground = B(theme.Text).Brush });
            var info = $"   {lang}  ·  {size:F1} KB" + (lines >= 0 ? $"  ·  {lines:N0} lines" : "");
            entry.Inlines.Add(new Run(info) { FontSize = 9, Foreground = B(theme.LineNum).Brush });
            doc.Blocks.Add(entry);
        }

        doc.Blocks.Add(new Paragraph { Margin = new Thickness(0, 16, 0, 0) });
    }

    // ── File section ────────────────────────────────────────────────────────

    private void AddFdFileSection(FlowDocument doc, JsonElement file, ExportOptions opts, PdfTheme theme)
    {
        var rel  = file.TryGetProperty("relative_path", out var rp) ? rp.GetString() ?? "" : "";
        var lang = file.TryGetProperty("language",      out var lg) && lg.ValueKind != JsonValueKind.Null ? lg.GetString() ?? "" : "";
        var ext  = file.TryGetProperty("extension",     out var ex) ? ex.GetString() ?? "" : "";
        var kb   = file.TryGetProperty("size",          out var sz) ? sz.GetInt64() / 1024.0 : 0;
        var lc   = file.TryGetProperty("line_count",    out var lci) && lci.ValueKind != JsonValueKind.Null ? lci.GetInt32() : -1;
        var mod  = file.TryGetProperty("last_modified", out var lm) ? lm.GetString()?.Split('T')[0] ?? "" : "";
        var isBin= file.TryGetProperty("is_binary",     out var ib) && ib.GetBoolean();
        var path = file.TryGetProperty("path",          out var pa) ? pa.GetString() ?? "" : "";

        // File header bar
        var headerPara = new Paragraph { Background = B(theme.AccentLight).Brush, Margin = new Thickness(0, 12, 0, 0), Padding = new Thickness(8, 4, 8, 4) };
        headerPara.Inlines.Add(new Run(rel) { FontWeight = FontWeights.Bold, FontSize = 11, Foreground = B(theme.Heading).Brush });
        doc.Blocks.Add(headerPara);

        // Metadata strip
        var metaPara = new Paragraph { Background = B(theme.CodeBg).Brush, Padding = new Thickness(8, 2, 8, 2), Margin = new Thickness(0, 0, 0, 6) };
        var metaText = $"{lang}   ·   {kb:F1} KB" + (lc >= 0 ? $"   ·   {lc:N0} lines" : "") + $"   ·   {mod}";
        metaPara.Inlines.Add(new Run(metaText) { FontSize = 8, FontStyle = FontStyles.Italic, Foreground = B(theme.LineNum).Brush });
        doc.Blocks.Add(metaPara);

        // Source code
        if (!isBin && System.IO.File.Exists(path))
        {
            try
            {
                var content  = System.IO.File.ReadAllText(path);
                var allLines = content.Split('\n');
                var maxLines = Math.Min(opts.MaxSourceLines, 200); // preview cap
                var display  = allLines.Take(maxLines).ToArray();

                // Get the right highlighter — try built-in first, then user extensions
                var builtIn    = GetBuiltInHighlighter(lang, ext);
                var userHl     = builtIn is null ? _langMgr.GetHighlighter(ext) : null;
                SyntaxHighlighter? hl = builtIn ?? (SyntaxHighlighter?)userHl;

                const int chunk = 50;
                for (var start = 0; start < display.Length; start += chunk)
                {
                    var slice = display.Skip(start).Take(chunk).ToArray();
                    var codePara = new Paragraph
                    {
                        FontFamily  = new FontFamily("Courier New"),
                        FontSize    = Math.Max(7, opts.FontSize - 1),
                        Background  = B(theme.CodeBg).Brush,
                        Foreground  = B(theme.CodeText).Brush,
                        Padding     = new Thickness(8, 2, 8, 2),
                        Margin      = new Thickness(0, 0, 0, 0),
                        BorderBrush = B(theme.Accent).Brush,
                        BorderThickness = new Thickness(3, 0, 0, 0),
                        LineHeight  = opts.FontSize + 3,
                    };

                    for (var i = 0; i < slice.Length; i++)
                    {
                        if (i > 0) codePara.Inlines.Add(new LineBreak());

                        if (opts.LineNumbers)
                        {
                            codePara.Inlines.Add(new Run($"{start + i + 1,4}  ")
                            {
                                Foreground = B(theme.LineNum).Brush,
                                FontSize   = Math.Max(6, opts.FontSize - 1.5)
                            });
                        }

                        var lineText = slice[i].Replace("\r", "").Replace("\t", "    ");
                        if (lineText.Length > 140) lineText = lineText[..137] + "…";

                        if (opts.SyntaxHighlighting && hl is not null)
                            EmitHighlightedWpf(codePara, lineText, hl, theme);
                        else
                            codePara.Inlines.Add(new Run(lineText));
                    }

                    doc.Blocks.Add(codePara);
                }

                if (allLines.Length > maxLines)
                    doc.Blocks.Add(new Paragraph(
                        new Run($"[ … {allLines.Length - maxLines:N0} more lines — export to see full file ]"))
                    { FontStyle = FontStyles.Italic, FontSize = 9, Foreground = B(theme.LineNum).Brush });
            }
            catch { doc.Blocks.Add(new Paragraph(new Run("[ Could not read file ]")) { FontStyle = FontStyles.Italic }); }
        }
        else if (isBin)
        {
            doc.Blocks.Add(new Paragraph(new Run("[ Binary file — not shown ]"))
                { FontStyle = FontStyles.Italic, Foreground = B(theme.LineNum).Brush });
        }
    }

    // ── Syntax highlight into WPF Inlines ──────────────────────────────────

    private static void EmitHighlightedWpf(Paragraph para, string line,
        SyntaxHighlighter hl, PdfTheme theme)
    {
        foreach (var (text, kind) in hl.Tokenize(line))
        {
            if (string.IsNullOrEmpty(text)) continue;
            var run = new Run(text)
            {
                Foreground  = kind switch
                {
                    TokenKind.Keyword      => B(theme.Kw).Brush,
                    TokenKind.String       => B(theme.Str).Brush,
                    TokenKind.Number       => B(theme.Num).Brush,
                    TokenKind.Comment      => B(theme.Cmt).Brush,
                    TokenKind.Type         => B(theme.Type).Brush,
                    TokenKind.Function     => B(theme.Fn).Brush,
                    TokenKind.Operator     => B(theme.Op).Brush,
                    TokenKind.Preprocessor => B(theme.Prep).Brush,
                    _                      => B(theme.CodeText).Brush,
                },
                FontStyle = kind == TokenKind.Comment ? FontStyles.Italic : FontStyles.Normal
            };
            para.Inlines.Add(run);
        }
    }

    // ── Export ──────────────────────────────────────────────────────────────

    private async Task ExportAsync()
    {
        var path = ProjectPathBox.Text.Trim();
        if (string.IsNullOrEmpty(path)) { MessageBox.Show("No project selected.", "QRD Preview"); return; }

        var outputDlg = new System.Windows.Forms.FolderBrowserDialog
            { Description = "Choose output folder", UseDescriptionForTitle = true, SelectedPath = _settings.OutputDir };
        if (outputDlg.ShowDialog() != System.Windows.Forms.DialogResult.OK) return;

        var opts = BuildOptions();

        try
        {
            SetStatus("Starting export…", busy: true);
            var payload = new
            {
                project_path = path,
                options = new
                {
                    mode              = "single",
                    output_path       = outputDlg.SelectedPath,
                    include_ai        = opts.IncludeAi,
                    include_toc       = opts.IncludeToc,
                    include_stats     = opts.IncludeStats,
                    include_charts    = opts.IncludeCharts,
                    syntax_highlighting = opts.SyntaxHighlighting,
                    line_numbers      = opts.LineNumbers,
                    theme             = opts.ThemeKey,
                    paper_size        = opts.PaperSize.ToString(),
                    orientation       = opts.Orientation == PdfOrientation.Portrait ? "portrait" : "landscape",
                    font_size         = opts.FontSize,
                },
                exclude_patterns = Array.Empty<string>()
            };

            var body = JsonSerializer.Serialize(payload);
            var resp = await _http.PostAsync("/export/start", new StringContent(body, Encoding.UTF8, "application/json"));
            resp.EnsureSuccessStatusCode();
            var jobId = JsonDocument.Parse(await resp.Content.ReadAsStringAsync()).RootElement.GetProperty("job_id").GetString();

            SetStatus($"Exporting… (job {jobId})", busy: true);

            // Poll until done
            while (true)
            {
                await Task.Delay(700);
                var sr = await _http.GetAsync($"/export/{jobId}/status");
                var sj = JsonDocument.Parse(await sr.Content.ReadAsStringAsync()).RootElement;
                var status   = sj.GetProperty("status").GetString() ?? "";
                var progress = sj.GetProperty("progress").GetDouble();
                SetStatus($"Exporting…  {progress:F0}%", busy: true);
                if (status is "completed" or "failed" or "cancelled")
                {
                    if (status == "completed")
                    {
                        SetStatus("Export complete! ✓", busy: false);
                        MessageBox.Show("PDF exported successfully.", "QRD — Export Complete",
                            MessageBoxButton.OK, MessageBoxImage.Information);
                    }
                    else
                    {
                        var err = sj.TryGetProperty("error", out var e) ? e.GetString() : status;
                        SetStatus($"Export {status}: {err}", busy: false);
                    }
                    break;
                }
            }
        }
        catch (Exception ex) { SetStatus($"Export failed: {ex.Message}", busy: false); }
    }

    // ── Helpers ─────────────────────────────────────────────────────────────

    private ExportOptions BuildOptions() => new()
    {
        IncludeToc         = ChkToc.IsChecked      == true,
        IncludeStats       = ChkStats.IsChecked    == true,
        IncludeAi          = ChkAi.IsChecked       == true,
        SyntaxHighlighting = ChkSyntax.IsChecked   == true,
        LineNumbers        = ChkLineNums.IsChecked  == true,
        FontSize           = (int)FontSizeSlider.Value,
        MaxSourceLines     = (int)MaxLinesSlider.Value,
        Theme              = (ThemeCombo.SelectedIndex) switch
        {
            1 => PdfThemeName.Dark, 2 => PdfThemeName.Github, 3 => PdfThemeName.Monokai, _ => PdfThemeName.Default
        },
        PaperSize  = OrientationCombo.SelectedIndex == 1 ? PaperSize.A4 : PaperSize.A4,
        Orientation= OrientationCombo.SelectedIndex == 1 ? PdfOrientation.Landscape : PdfOrientation.Portrait,
        CoverNote  = CoverNoteBox?.Text ?? "",
    };

    private void SetStatus(string msg, bool busy)
    {
        Dispatcher.Invoke(() =>
        {
            StatusLabel.Text                  = msg;
            PreviewInfoLabel.Text             = msg;
            PreviewProgress.Visibility        = busy ? Visibility.Visible : Visibility.Collapsed;
        });
    }

    private static List<JsonElement> GetOrderedFiles(JsonElement scan)
    {
        if (!scan.TryGetProperty("flat_files", out var flat)) return [];
        var all   = flat.EnumerateArray().ToList();
        var root  = all.Where(f => { var r = f.TryGetProperty("relative_path", out var rp) ? rp.GetString() ?? "" : ""; return !r.Contains('/'); })
                       .OrderBy(f => f.TryGetProperty("name", out var n) ? n.GetString() : "").ToList();
        var others= all.Where(f => { var r = f.TryGetProperty("relative_path", out var rp) ? rp.GetString() ?? "" : ""; return r.Contains('/'); })
                       .OrderBy(f => f.TryGetProperty("relative_path", out var rp) ? rp.GetString() : "").ToList();
        root.AddRange(others);
        return root;
    }

    private static SyntaxHighlighter? GetBuiltInHighlighter(string lang, string ext)
    {
        return lang.ToLowerInvariant() switch
        {
            "csharp"     => new CSharpHighlighter(),
            "python"     => new PythonHighlighter(),
            "javascript" => new JsHighlighter(),
            "typescript" => new JsHighlighter(),
            "reacttsx"   => new JsHighlighter(),
            "sql"        => new SqlHighlighter(),
            "yaml"       => new YamlHighlighter(),
            "json"       => new JsonHighlighter(),
            "xml"        => new XmlHighlighter(),
            "html"       => new XmlHighlighter(),
            "shell"      => new ShellHighlighter(),
            "bash"       => new ShellHighlighter(),
            "css"        => new CssHighlighter(),
            "java"       => new CSharpHighlighter(),
            "go"         => new CSharpHighlighter(),
            "rust"       => new CSharpHighlighter(),
            "cpp"        => new CSharpHighlighter(),
            _            => null
        };
    }

    // Color helper — parse hex → SolidColorBrush wrapper
    private static (SolidColorBrush Brush) B(string hex)
    {
        hex = hex.TrimStart('#');
        if (hex.Length == 6)
        {
            var r = Convert.ToByte(hex[..2], 16);
            var g = Convert.ToByte(hex[2..4], 16);
            var b = Convert.ToByte(hex[4..6], 16);
            return (new SolidColorBrush(Color.FromRgb(r, g, b)));
        }
        return (Brushes.Black);
    }

    private static Color ParseWpfColor(string hex)
    {
        hex = hex.TrimStart('#');
        if (hex.Length != 6) return Colors.White;
        return Color.FromRgb(Convert.ToByte(hex[..2], 16), Convert.ToByte(hex[2..4], 16), Convert.ToByte(hex[4..6], 16));
    }
}
