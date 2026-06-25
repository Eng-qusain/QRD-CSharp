using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using QRD.Utils;

namespace QRD.UI.Views;

public partial class SettingsPage : Page
{
    private readonly AppSettings _settings;

    public SettingsPage(AppSettings settings)
    {
        InitializeComponent();
        _settings = settings;
        LoadCurrentSettings();

        WorkerSlider.ValueChanged      += (_, e) => WorkerLabel.Text      = $"Workers: {(int)e.NewValue}";
        MaxFileSizeSlider.ValueChanged += (_, e) => MaxFileSizeLabel.Text = $"{(int)e.NewValue} MB";
    }

    private void LoadCurrentSettings()
    {
        AnthropicKeyBox.Text    = _settings.AnthropicApiKey ?? "";
        OpenAiKeyBox.Text       = _settings.OpenAiApiKey    ?? "";
        OutputDirBox.Text       = _settings.OutputDir;
        WorkerSlider.Value      = _settings.MaxConcurrentWorkers;
        MaxFileSizeSlider.Value = _settings.MaxFileSizeMb;

        // Select the model in the combo box
        for (var i = 0; i < ModelCombo.Items.Count; i++)
        {
            if (ModelCombo.Items[i] is ComboBoxItem ci &&
                ci.Content?.ToString() == _settings.AiModel)
            {
                ModelCombo.SelectedIndex = i;
                break;
            }
        }
    }

    // ── Save handlers ──────────────────────────────────────────────────────────

    private void SaveAiSettings_Click(object sender, RoutedEventArgs e)
    {
        _settings.AnthropicApiKey = AnthropicKeyBox.Text.Trim().NullIfEmpty();
        _settings.OpenAiApiKey    = OpenAiKeyBox.Text.Trim().NullIfEmpty();
        _settings.AiModel = (ModelCombo.SelectedItem as ComboBoxItem)?
                            .Content?.ToString() ?? "claude-3-5-haiku-20241022";
        PersistSettings();
        MessageBox.Show("AI settings saved.\nRestart QRD for the new key to take effect.",
            "QRD — Settings Saved");
    }

    private void SavePathSettings_Click(object sender, RoutedEventArgs e)
    {
        _settings.OutputDir = OutputDirBox.Text.Trim();
        PersistSettings();
        MessageBox.Show("Path settings saved.", "QRD — Settings Saved");
    }

    private void BrowseOutputDir_Click(object sender, RoutedEventArgs e)
    {
        using var dlg = new System.Windows.Forms.FolderBrowserDialog
        {
            Description            = "Select default output folder",
            UseDescriptionForTitle = true,
            SelectedPath           = OutputDirBox.Text
        };
        if (dlg.ShowDialog() == System.Windows.Forms.DialogResult.OK)
            OutputDirBox.Text = dlg.SelectedPath;
    }

    // ── Persistence ────────────────────────────────────────────────────────────

    private void PersistSettings()
    {
        var path = Path.Combine(AppContext.BaseDirectory, "appsettings.json");
        var obj = new
        {
            Host                 = _settings.Host,
            Port                 = _settings.Port,
            Env                  = _settings.Env,
            AnthropicApiKey      = _settings.AnthropicApiKey,
            OpenAiApiKey         = _settings.OpenAiApiKey,
            AiModel              = _settings.AiModel,
            AiEnabled            = _settings.AiEnabled,
            OutputDir            = _settings.OutputDir,
            TempDir              = _settings.TempDir,
            MaxFileSizeMb        = (int)MaxFileSizeSlider.Value,
            MaxConcurrentWorkers = (int)WorkerSlider.Value
        };
        File.WriteAllText(path,
            JsonSerializer.Serialize(obj, new JsonSerializerOptions { WriteIndented = true }));
    }
}

internal static class StringExtensions
{
    public static string? NullIfEmpty(this string s)
        => string.IsNullOrWhiteSpace(s) ? null : s;
}
