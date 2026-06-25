using System.Net.Http;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using QRD.Utils;

namespace QRD.UI.Views;

public partial class MainWindow : Window
{
    private readonly AppSettings _settings;
    private readonly HttpClient  _http;

    public MainWindow(AppSettings settings)
    {
        InitializeComponent();
        _settings = settings;
        _http     = new HttpClient
            { BaseAddress = new Uri($"http://{settings.Host}:{settings.Port}") };

        // Show dashboard by default
        NavigateTo(new DashboardPage(_settings));
        _ = CheckApiStatusAsync();
    }

    // ── Navigation ─────────────────────────────────────────────────────────────

    private void NavDashboard_Click(object sender, RoutedEventArgs e)
        => NavigateTo(new DashboardPage(_settings));

    private void NavScanner_Click(object sender, RoutedEventArgs e)
        => NavigateTo(new ScannerPage(_settings));

    private void NavExport_Click(object sender, RoutedEventArgs e)
        => NavigateTo(new ExportPage(_settings));

    private void NavSettings_Click(object sender, RoutedEventArgs e)
        => NavigateTo(new SettingsPage(_settings));

    private void NavigateTo(Page page) => ContentFrame.Navigate(page);

    // ── API health status indicator ────────────────────────────────────────────

    private async Task CheckApiStatusAsync()
    {
        // Retry a few times — server takes ~400 ms to start
        for (var attempt = 0; attempt < 5; attempt++)
        {
            try
            {
                var resp = await _http.GetAsync("/health");
                if (resp.IsSuccessStatusCode)
                {
                    var body = await resp.Content.ReadAsStringAsync();
                    var doc  = JsonDocument.Parse(body).RootElement;
                    var aiOk = doc.TryGetProperty("ai_available", out var v) && v.GetBoolean();

                    ApiStatusText.Text       = aiOk ? "✓ AI enabled" : "⚠ No AI key";
                    ApiStatusText.Foreground = aiOk
                        ? new SolidColorBrush(Color.FromRgb(0x3F, 0xB9, 0x50))
                        : new SolidColorBrush(Color.FromRgb(0xD2, 0x93, 0x22));
                    return;
                }
            }
            catch { /* server still starting */ }

            await Task.Delay(600);
        }

        ApiStatusText.Text = "⚠ API unavailable";
    }
}
