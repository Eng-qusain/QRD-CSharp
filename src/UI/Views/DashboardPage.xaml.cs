using System.Net.Http;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using QRD.Utils;

namespace QRD.UI.Views;

public partial class DashboardPage : Page
{
    public DashboardPage(AppSettings settings)
    {
        InitializeComponent();
        CheckAiStatus(settings);
    }

    private async void CheckAiStatus(AppSettings settings)
    {
        try
        {
            using var http = new HttpClient
            { BaseAddress = new Uri($"http://{settings.Host}:{settings.Port}") };
            var resp = await http.GetAsync("/health");
            if (resp.IsSuccessStatusCode)
            {
                var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync()).RootElement;
                if (doc.TryGetProperty("ai_available", out var v) && v.GetBoolean())
                    AiSetupHint.Visibility = Visibility.Collapsed;
            }
        }
        catch { /* ignore — server may still be starting */ }
    }
}
