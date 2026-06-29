using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using QRD.Core.Infrastructure.Syntax;
using QRD.Utils;
using MessageBox = System.Windows.MessageBox;

namespace QRD.UI.Views;

public partial class SettingsPage : Page
{
    private readonly AppSettings              _settings;
    private readonly LanguageExtensionManager _langMgr;

    public SettingsPage(AppSettings settings, LanguageExtensionManager langMgr)
    {
        InitializeComponent();
        _settings = settings;
        _langMgr  = langMgr;

        LoadCurrentSettings();
        RefreshLangList();

        WorkerSlider.ValueChanged     += (_, e) => WorkerLabel.Text     = $"Workers: {(int)e.NewValue}";
        MaxFileSizeSlider.ValueChanged += (_, e) => MaxFileSizeLabel.Text = $"{(int)e.NewValue} MB";
    }

    // ── Load UI from current settings ──────────────────────────────────────

    private void LoadCurrentSettings()
    {
        AnthropicKeyBox.Text = _settings.AnthropicApiKey ?? "";
        OpenAiKeyBox.Text    = _settings.OpenAiApiKey    ?? "";
        OutputDirBox.Text    = _settings.OutputDir;
        WorkerSlider.Value       = _settings.MaxConcurrentWorkers;
        MaxFileSizeSlider.Value  = _settings.MaxFileSizeMb;

        for (var i = 0; i < ModelCombo.Items.Count; i++)
            if (ModelCombo.Items[i] is ComboBoxItem ci && ci.Content?.ToString() == _settings.AiModel)
            { ModelCombo.SelectedIndex = i; break; }
    }

    // ── AI settings ────────────────────────────────────────────────────────

    private void SaveAiSettings_Click(object sender, RoutedEventArgs e)
    {
        _settings.AnthropicApiKey = AnthropicKeyBox.Text.Trim().NullIfEmpty();
        _settings.OpenAiApiKey    = OpenAiKeyBox.Text.Trim().NullIfEmpty();
        _settings.AiModel         = (ModelCombo.SelectedItem as ComboBoxItem)?.Content?.ToString()
                                    ?? "claude-3-5-haiku-20241022";
        PersistSettings();
        MessageBox.Show("AI settings saved.\nRestart QRD for the new key to take effect.",
                        "QRD — Settings Saved");
    }

    // ── Path settings ───────────────────────────────────────────────────────

    private void SavePathSettings_Click(object sender, RoutedEventArgs e)
    {
        _settings.OutputDir = OutputDirBox.Text.Trim();
        PersistSettings();
        MessageBox.Show("Path settings saved.", "QRD — Settings Saved");
    }

    private void BrowseOutputDir_Click(object sender, RoutedEventArgs e)
    {
        using var dlg = new System.Windows.Forms.FolderBrowserDialog
            { Description = "Select default output folder", UseDescriptionForTitle = true, SelectedPath = OutputDirBox.Text };
        if (dlg.ShowDialog() == System.Windows.Forms.DialogResult.OK)
            OutputDirBox.Text = dlg.SelectedPath;
    }

    // ── Language Extensions ────────────────────────────────────────────────

    private void AddLanguage_Click(object sender, RoutedEventArgs e)
    {
        var name = LangNameBox.Text.Trim();
        var exts = LangExtBox.Text.Trim();

        if (string.IsNullOrEmpty(name) || string.IsNullOrEmpty(exts))
        {
            MessageBox.Show("Name and Extensions are required.", "QRD — Language Extension");
            return;
        }

        // Build new entry
        var entry = new
        {
            Name               = name,
            Extensions         = exts,
            LineComment        = LangLineCommentBox.Text.Trim(),
            BlockCommentStart  = LangBlockStartBox.Text.Trim(),
            BlockCommentEnd    = LangBlockEndBox.Text.Trim(),
            Keywords           = LangKeywordsBox.Text.Replace("\r\n", ",").Replace("\n", ",").Trim(),
            StringDelimiters   = LangStringBox.Text.Trim(),
            TypePattern        = LangTypePatBox.Text.Trim(),
            FunctionPattern    = LangFnPatBox.Text.Trim(),
        };

        // Merge into appsettings.json
        var path    = Path.Combine(AppContext.BaseDirectory, "appsettings.json");
        var rawJson = File.Exists(path) ? File.ReadAllText(path) : "{}";

        using var doc  = JsonDocument.Parse(rawJson);
        var root       = doc.RootElement;
        var allProps   = new Dictionary<string, JsonElement>();

        foreach (var prop in root.EnumerateObject())
            allProps[prop.Name] = prop.Value;

        // Build updated LanguageExtensions array
        var existing = new List<object>();
        if (root.TryGetProperty("LanguageExtensions", out var arr))
            foreach (var el in arr.EnumerateArray())
                existing.Add(JsonSerializer.Deserialize<object>(el.GetRawText())!);

        existing.Add(entry);

        var newDoc = new Dictionary<string, object>();
        foreach (var kv in allProps)
            newDoc[kv.Key] = JsonSerializer.Deserialize<object>(kv.Value.GetRawText())!;
        newDoc["LanguageExtensions"] = existing;

        File.WriteAllText(path, JsonSerializer.Serialize(newDoc,
            new JsonSerializerOptions { WriteIndented = true }));

        _langMgr.Reload();
        RefreshLangList();
        ClearLangForm();

        MessageBox.Show($"Language '{name}' added and saved to appsettings.json.",
                        "QRD — Language Added");
    }

    private void RemoveLanguage_Click(object sender, RoutedEventArgs e)
    {
        if (LangList.SelectedItem is not string selected) return;
        var langName = selected.Split('—')[0].Trim();

        var path    = Path.Combine(AppContext.BaseDirectory, "appsettings.json");
        if (!File.Exists(path)) return;

        using var doc  = JsonDocument.Parse(File.ReadAllText(path));
        var root       = doc.RootElement;

        var newDoc = new Dictionary<string, object>();
        foreach (var prop in root.EnumerateObject())
        {
            if (prop.Name == "LanguageExtensions")
            {
                var kept = prop.Value.EnumerateArray()
                    .Where(el => el.TryGetProperty("Name", out var n) && n.GetString() != langName)
                    .Select(el => JsonSerializer.Deserialize<object>(el.GetRawText())!)
                    .ToList();
                newDoc["LanguageExtensions"] = kept;
            }
            else
            {
                newDoc[prop.Name] = JsonSerializer.Deserialize<object>(prop.Value.GetRawText())!;
            }
        }

        File.WriteAllText(path, JsonSerializer.Serialize(newDoc,
            new JsonSerializerOptions { WriteIndented = true }));

        _langMgr.Reload();
        RefreshLangList();
    }

    private void ReloadLanguages_Click(object sender, RoutedEventArgs e)
    {
        _langMgr.Reload();
        RefreshLangList();
        MessageBox.Show($"Reloaded {_langMgr.Languages.Count} language extension(s).",
                        "QRD — Languages Reloaded");
    }

    private void RefreshLangList()
    {
        LangList.ItemsSource = _langMgr.Languages
            .Select(l => $"{l.Name}  —  {string.Join(", ", l.Extensions)}")
            .ToList();
    }

    private void ClearLangForm()
    {
        LangNameBox.Text        = "";
        LangExtBox.Text         = "";
        LangLineCommentBox.Text = "";
        LangBlockStartBox.Text  = "";
        LangBlockEndBox.Text    = "";
        LangKeywordsBox.Text    = "";
        LangStringBox.Text      = "\",'";
        LangTypePatBox.Text     = @"\b[A-Z][A-Za-z0-9_]*\b";
        LangFnPatBox.Text       = "";
    }

    // ── Persist all settings ────────────────────────────────────────────────

    private void PersistSettings()
    {
        var path = Path.Combine(AppContext.BaseDirectory, "appsettings.json");
        var existingJson = File.Exists(path) ? File.ReadAllText(path) : "{}";

        // Preserve LanguageExtensions and other unknown keys
        using var existing = JsonDocument.Parse(existingJson);
        var merged = new Dictionary<string, object?>();
        foreach (var prop in existing.RootElement.EnumerateObject())
            merged[prop.Name] = JsonSerializer.Deserialize<object>(prop.Value.GetRawText());

        // Overwrite known settings keys
        merged["Host"]                = _settings.Host;
        merged["Port"]                = _settings.Port;
        merged["Env"]                 = _settings.Env;
        merged["AnthropicApiKey"]     = _settings.AnthropicApiKey;
        merged["OpenAiApiKey"]        = _settings.OpenAiApiKey;
        merged["AiModel"]             = _settings.AiModel;
        merged["AiEnabled"]           = _settings.AiEnabled;
        merged["OutputDir"]           = _settings.OutputDir;
        merged["TempDir"]             = _settings.TempDir;
        merged["MaxFileSizeMb"]       = (int)MaxFileSizeSlider.Value;
        merged["MaxConcurrentWorkers"]= (int)WorkerSlider.Value;

        File.WriteAllText(path, JsonSerializer.Serialize(merged,
            new JsonSerializerOptions { WriteIndented = true }));
    }
}

internal static class StringExtensions
{
    public static string? NullIfEmpty(this string s)
        => string.IsNullOrWhiteSpace(s) ? null : s;
}
