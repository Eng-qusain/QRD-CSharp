using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using QRD.Utils;

namespace QRD.UI.Views;

public partial class ScannerPage : Page
{
    private readonly HttpClient _http;
    private JsonElement?        _lastScan;

    public ScannerPage(AppSettings settings)
    {
        InitializeComponent();
        _http = new HttpClient
            { BaseAddress = new Uri($"http://{settings.Host}:{settings.Port}") };
    }

    // ── Handlers ───────────────────────────────────────────────────────────────

    private void Browse_Click(object sender, RoutedEventArgs e)
    {
        using var dlg = new System.Windows.Forms.FolderBrowserDialog
        {
            Description            = "Select project folder",
            UseDescriptionForTitle = true
        };
        if (dlg.ShowDialog() == System.Windows.Forms.DialogResult.OK)
            PathBox.Text = dlg.SelectedPath;
    }

    private async void Scan_Click(object sender, RoutedEventArgs e)
    {
        var path = PathBox.Text.Trim();
        if (string.IsNullOrEmpty(path))
        {
            MessageBox.Show("Please enter or browse to a project folder.", "QRD");
            return;
        }

        ScanButton.IsEnabled     = false;
        ProgressPanel.Visibility = Visibility.Visible;
        ScanProgress.Value       = 0;
        ProgressText.Text        = "Starting scan…";
        FileTree.Items.Clear();
        DetailPanel.Children.Clear();

        try
        {
            var json    = JsonSerializer.Serialize(new { path, exclude_patterns = Array.Empty<string>() });
            var content = new StringContent(json, Encoding.UTF8, "application/json");

            using var cts = new CancellationTokenSource();
            var anim = AnimateProgressAsync(cts.Token);

            var resp = await _http.PostAsync("/scanner/scan", content);
            cts.Cancel();
            try { await anim; } catch { }

            resp.EnsureSuccessStatusCode();
            var body  = await resp.Content.ReadAsStringAsync();
            _lastScan = JsonDocument.Parse(body).RootElement;

            ScanProgress.Value = 100;
            ProgressText.Text  = "Scan complete!";
            PopulateFileTree(_lastScan.Value);
            ShowProjectStats(_lastScan.Value);
        }
        catch (Exception ex)
        {
            ProgressText.Text = $"Error: {ex.Message}";
            MessageBox.Show($"Scan failed:\n{ex.Message}", "QRD Error",
                MessageBoxButton.OK, MessageBoxImage.Error);
        }
        finally
        {
            ScanButton.IsEnabled = true;
            await Task.Delay(1800);
            ProgressPanel.Visibility = Visibility.Collapsed;
        }
    }

    private async Task AnimateProgressAsync(CancellationToken ct)
    {
        for (var i = 0; i < 88 && !ct.IsCancellationRequested; i += 3)
        {
            ScanProgress.Value = i;
            ProgressText.Text  = $"Scanning… {i}%";
            await Task.Delay(180, ct);
        }
    }

    private void FileTree_SelectedItemChanged(
        object sender, RoutedPropertyChangedEventArgs<object> e)
    {
        if (e.NewValue is TreeViewItem { Tag: JsonElement file })
            ShowFileDetail(file);
    }

    // ── Tree Building ──────────────────────────────────────────────────────────

    private void PopulateFileTree(JsonElement result)
    {
        if (!result.TryGetProperty("file_tree", out var tree)) return;
        var root = BuildTreeItem(tree);
        if (root is null) return;
        root.IsExpanded = true;
        FileTree.Items.Add(root);
    }

    private static TreeViewItem? BuildTreeItem(JsonElement node)
    {
        if (!node.TryGetProperty("name", out var nameProp)) return null;
        var name  = nameProp.GetString() ?? "";
        var isDir = node.TryGetProperty("type", out var t) && t.GetString() == "directory";

        var item = new TreeViewItem
        {
            Header = (isDir ? "📁 " : "📄 ") + name,
            Tag    = isDir ? null : (object?)node
        };

        if (node.TryGetProperty("children", out var children))
            foreach (var child in children.EnumerateArray())
            {
                var ci = BuildTreeItem(child);
                if (ci is not null) item.Items.Add(ci);
            }

        return item;
    }

    // ── Detail Panel ───────────────────────────────────────────────────────────

    private void ShowProjectStats(JsonElement result)
    {
        DetailPanel.Children.Clear();
        DetailHeader.Text = "Project Statistics";

        if (!result.TryGetProperty("stats", out var stats)) return;

        if (stats.TryGetProperty("total_files",       out var tf))
            AddRow("Total Files",  $"{tf.GetInt64():N0}");
        if (stats.TryGetProperty("total_lines",       out var tl))
            AddRow("Total Lines",  $"{tl.GetInt64():N0}");
        if (stats.TryGetProperty("total_size",        out var ts))
            AddRow("Total Size",   $"{ts.GetInt64() / 1_048_576.0:F1} MB");
        if (stats.TryGetProperty("total_directories", out var td))
            AddRow("Directories",  $"{td.GetInt64():N0}");

        if (!stats.TryGetProperty("language_distribution", out var langs)) return;
        DetailPanel.Children.Add(new TextBlock
        {
            Text       = "Language Distribution",
            FontWeight = FontWeights.SemiBold,
            Margin     = new Thickness(0, 16, 0, 8)
        });
        foreach (var lang in langs.EnumerateObject()
                                  .OrderByDescending(p => p.Value.GetInt32())
                                  .Take(10))
            AddRow(lang.Name, $"{lang.Value.GetInt32()} files");
    }

    private void ShowFileDetail(JsonElement file)
    {
        DetailPanel.Children.Clear();
        DetailHeader.Text = file.TryGetProperty("name", out var n)
            ? n.GetString() ?? "File" : "File";

        if (file.TryGetProperty("relative_path", out var rp))
            AddRow("Path",     rp.GetString() ?? "");
        if (file.TryGetProperty("language", out var lang) &&
            lang.ValueKind != JsonValueKind.Null)
            AddRow("Language", lang.GetString() ?? "");
        if (file.TryGetProperty("size", out var sz))
            AddRow("Size",     $"{sz.GetInt64() / 1024.0:F1} KB");
        if (file.TryGetProperty("line_count", out var lc) &&
            lc.ValueKind != JsonValueKind.Null)
            AddRow("Lines",    $"{lc.GetInt32():N0}");
        if (file.TryGetProperty("last_modified", out var lm))
            AddRow("Modified", (lm.GetString() ?? "").Split('T')[0]);
        if (file.TryGetProperty("category", out var cat))
            AddRow("Category", cat.GetString() ?? "");
    }

    private void AddRow(string label, string value)
    {
        var sp = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Margin      = new Thickness(0, 3, 0, 0)
        };
        sp.Children.Add(new TextBlock
        {
            Text       = label + ": ",
            Foreground = System.Windows.Media.Brushes.Gray,
            FontSize   = 12,
            MinWidth   = 95
        });
        sp.Children.Add(new TextBlock { Text = value, FontSize = 12 });
        DetailPanel.Children.Add(sp);
    }
}
