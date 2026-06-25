using System.IO;
using Microsoft.Extensions.Configuration;

namespace QRD.Utils;

/// <summary>
/// Application configuration loaded from appsettings.json and environment variables.
/// Equivalent to the Python pydantic-settings <c>Settings</c> class.
/// </summary>
public class AppSettings
{
    // Server
    public string Host { get; set; } = "127.0.0.1";
    public int Port { get; set; } = 8765;
    public string Env { get; set; } = "production";

    // Logging
    public string LogLevel { get; set; } = "Information";
    public string? LogFile { get; set; }

    // AI
    public string? AnthropicApiKey { get; set; }
    public string? OpenAiApiKey { get; set; }
    public string AiModel { get; set; } = "claude-3-5-haiku-20241022";
    public bool AiEnabled { get; set; } = true;

    // Paths
    public string TempDir { get; set; } =
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".qrd", "temp");
    public string OutputDir { get; set; } =
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), "QRD");

    // Performance
    public int MaxFileSizeMb { get; set; } = 50;
    public int MaxConcurrentWorkers { get; set; } = 4;
    public int ScanChunkSize { get; set; } = 500;

    public bool IsDevelopment => Env == "development";
    public long MaxFileSizeBytes => (long)MaxFileSizeMb * 1024 * 1024;

    public static AppSettings Load()
    {
        var config = new ConfigurationBuilder()
            .SetBasePath(AppContext.BaseDirectory)
            .AddJsonFile("appsettings.json", optional: true)
            .AddEnvironmentVariables(prefix: "QRD_")
            .Build();

        var settings = new AppSettings();
        config.Bind(settings);

        // Allow env-var override without prefix for API keys (common pattern)
        settings.AnthropicApiKey ??= Environment.GetEnvironmentVariable("ANTHROPIC_API_KEY");
        settings.OpenAiApiKey ??= Environment.GetEnvironmentVariable("OPENAI_API_KEY");

        return settings;
    }
}
