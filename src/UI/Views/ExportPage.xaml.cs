using System.Diagnostics;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using QRD.Utils;

namespace QRD.UI.Views;

public partial class ExportPage : Page
{
    private readonly HttpClient _http;
    private readonly AppSettings _settings;
    private string? _currentJobId;
    private CancellationTokenSource? _pollCts;

    // v2: explicit string literals matching union types (no "as any")
    private static readonly string[] ThemeValues      = ["default", "dark", "github", "monokai"];
    private static readonly string[] PaperSizeValues  = ["A4", "Letter", "A3"];
    private static readonly string[] OrientationValues = ["portrait", "landscape"];

    public ExportPage(AppSettings settings)
    {
        InitializeComponent();
        _settings = settings;
        _http = new HttpClient { BaseAddress = new Uri($"http://{settings.Host}:{settings.Port}") };
        OutputPathBox.Text = settings.OutputDir;
    }

    // ── Browse ────────────────────────────────────────────────────────────────

    private void BrowseProject_Click(object sender, RoutedEventArgs e)
    {
        var dlg = new System.Windows.Forms.FolderBrowserDialog
            { Description = "Select project folder", UseDescriptionForTitle = true };
        if (dlg.ShowDialog() == System.Windows.Forms.DialogResult.OK)
            ProjectPathBox.Text = dlg.SelectedPath;
    }

    private void BrowseOutput_Click(object sender, RoutedEventArgs e)
    {
        var dlg = new System.Windows.Forms.FolderBrowserDialog
            { Description = "Select output folder", UseDescriptionForTitle = true };
        if (dlg.ShowDialog() == System.Windows.Forms.DialogResult.OK)
            OutputPathBox.Text = dlg.SelectedPath;
    }

    // ── Export ────────────────────────────────────────────────────────────────

    private async void StartExport_Click(object sender, RoutedEventArgs e)
    {
        var projectPath = ProjectPathBox.Text.Trim();
        var outputPath  = OutputPathBox.Text.Trim();

        if (string.IsNullOrEmpty(projectPath))
        {
            MessageBox.Show("Please select a project folder.", "QRD");
            return;
        }

        // v2: strongly-typed string literals, no "as any"
        var mode        = ModeSingle.IsChecked == true  ? "single"
                        : ModeFolder.IsChecked == true  ? "folder"
                        : ModeFile.IsChecked   == true  ? "file"
                        :                                 "package";

        var theme       = ThemeValues[ThemeCombo.SelectedIndex];
        var paper       = PaperSizeValues[PaperCombo.SelectedIndex];
        var orientation = OrientationValues[OrientationCombo.SelectedIndex];

        var payload = new
        {
            project_path = projectPath,
            options = new
            {
                mode,
                output_path         = outputPath,
                include_ai          = IncludeAi.IsChecked       == true,
                include_toc         = IncludeToc.IsChecked       == true,
                include_stats       = IncludeStats.IsChecked     == true,
                include_charts      = IncludeCharts.IsChecked    == true,
                syntax_highlighting = SyntaxHighlight.IsChecked  == true,
                line_numbers        = LineNumbers.IsChecked       == true,
                theme,
                paper_size          = paper,
                orientation,
                font_size           = 9
            },
            exclude_patterns = Array.Empty<string>()
        };

        ExportButton.IsEnabled = false;
        ExportButton.Visibility = Visibility.Collapsed;
        CancelButton.Visibility = Visibility.Visible;
        ProgressPanel.Visibility = Visibility.Visible;
        OutputFilesPanel.Visibility = Visibility.Collapsed;
        OutputFilesList.ItemsSource = null;
        ExportProgress.Value = 0;
        ExportStatus.Text = "Starting…";

        try
        {
            var body    = JsonSerializer.Serialize(payload);
            var content = new StringContent(body, Encoding.UTF8, "application/json");
            var resp    = await _http.PostAsync("/export/start", content);
            resp.EnsureSuccessStatusCode();

            var json      = JsonDocument.Parse(await resp.Content.ReadAsStringAsync()).RootElement;
            _currentJobId = json.GetProperty("job_id").GetString();

            _pollCts = new CancellationTokenSource();
            await PollJobStatusAsync(_currentJobId!, _pollCts.Token);
        }
        catch (Exception ex)
        {
            ExportStatus.Text = $"✗ Error: {ex.Message}";
            MessageBox.Show($"Export failed to start: {ex.Message}", "QRD Error",
                MessageBoxButton.OK, MessageBoxImage.Error);
        }
        finally
        {
            ExportButton.IsEnabled = true;
            ExportButton.Visibility = Visibility.Visible;
            CancelButton.Visibility = Visibility.Collapsed;
        }
    }

    private async Task PollJobStatusAsync(string jobId, CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            try
            {
                var resp = await _http.GetAsync($"/export/{jobId}/status", ct);
                if (!resp.IsSuccessStatusCode) break;

                var json        = JsonDocument.Parse(await resp.Content.ReadAsStringAsync(ct)).RootElement;
                var status      = json.GetProperty("status").GetString() ?? "";
                var progress    = json.GetProperty("progress").GetDouble();
                var currentFile = json.TryGetProperty("current_file", out var cf) ? cf.GetString() ?? "" : "";

                ExportProgress.Value = progress;
                ExportStatus.Text = $"{progress:F0}%  {currentFile}";

                if (status is "completed" or "failed" or "cancelled")
                {
                    if (status == "completed")
                    {
                        ExportStatus.Text = "✓ Export complete!";
                        if (json.TryGetProperty("output_files", out var files))
                        {
                            var fileList = files.EnumerateArray()
                                               .Select(f => f.GetString() ?? "")
                                               .Where(f => f != "")
                                               .ToList();
                            OutputFilesList.ItemsSource = fileList;
                            OutputFilesPanel.Visibility = Visibility.Visible;
                        }
                    }
                    else if (status == "failed")
                    {
                        var error = json.TryGetProperty("error", out var err)
                            ? err.GetString()
                            : "Unknown error";
                        ExportStatus.Text = $"✗ Export failed: {error}";
                    }
                    else
                    {
                        ExportStatus.Text = "Export cancelled.";
                    }
                    break;
                }

                await Task.Delay(500, ct);
            }
            catch (OperationCanceledException) { break; }
            catch { break; }
        }
    }

    private async void Cancel_Click(object sender, RoutedEventArgs e)
    {
        _pollCts?.Cancel();
        if (_currentJobId is not null)
        {
            try { await _http.PostAsync($"/export/{_currentJobId}/cancel", null); }
            catch { /* ignore */ }
        }
        ExportStatus.Text = "Cancelling…";
    }

    private void OpenOutputFile(object sender, System.Windows.Input.MouseButtonEventArgs e)
    {
        if (sender is System.Windows.Controls.TextBlock { Text: { } path })
        {
            try { Process.Start(new ProcessStartInfo(path) { UseShellExecute = true }); }
            catch { /* ignore */ }
        }
    }
}
