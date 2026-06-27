using System.Text.Json;
using Anthropic.SDK;
using Anthropic.SDK.Messaging;
using QRD.Core.Domain.Entities;
using QRD.Utils;

namespace QRD.Core.Infrastructure.AI;

/// <summary>
/// Generates AI documentation for source files using Anthropic.SDK v3.1.0.
/// - With Anthropic key  → uses Claude via GetClaudeMessageAsync
/// - Without any key     → returns clean stub documentation (no crash)
/// </summary>
public class AIDocumenter
{
    private readonly AppSettings _settings;
    private readonly AnthropicClient? _anthropicClient;
    private readonly bool _available;

    private const string SystemPrompt =
        "You are a senior software engineer generating structured technical documentation. " +
        "Analyze the provided source code and return ONLY valid JSON (no markdown, no explanation). " +
        "Return this exact JSON structure: " +
        "{\"summary\":\"One-sentence description\",\"purpose\":\"2-3 sentence role explanation\"," +
        "\"key_functions\":[\"name: what it does\"],\"inputs\":[\"description\"]," +
        "\"outputs\":[\"description\"],\"dependencies\":[\"package\"]," +
        "\"complexity\":\"Low|Medium|High|Very High\",\"notes\":\"any important notes\"}";

    public AIDocumenter(AppSettings settings)
    {
        _settings = settings;
        if (!string.IsNullOrWhiteSpace(settings.AnthropicApiKey))
        {
            _anthropicClient = new AnthropicClient(settings.AnthropicApiKey);
            _available = true;
        }
    }

    public bool IsAvailable => _available;

    public async Task<AIDocumentation> DocumentFileAsync(
        string fileId,
        string filePath,
        string content,
        string language,
        int maxContentChars = 8000,
        CancellationToken ct = default)
    {
        if (!_available)
            return StubDocumentation(fileId, filePath, language);

        var truncated = content.Length > maxContentChars
            ? content[..maxContentChars] + $"\n\n... [truncated at {maxContentChars} chars]"
            : content;

        var userMessage = $"File: {filePath}\nLanguage: {language}\n\n```{language}\n{truncated}\n```";

        try
        {
            var raw = await CallAnthropicAsync(userMessage, ct);
            if (raw is not null)
            {
                var doc = ParseJsonResponse(raw);
                doc.FileId      = fileId;
                doc.FilePath    = filePath;
                doc.GeneratedAt = DateTime.UtcNow;
                doc.ModelUsed   = _settings.AiModel;
                return doc;
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[AI] Documentation failed for {filePath}: {ex.Message}");
        }

        return StubDocumentation(fileId, filePath, language);
    }

    private async Task<string?> CallAnthropicAsync(string userMessage, CancellationToken ct)
    {
        if (_anthropicClient is null) throw new InvalidOperationException("Anthropic client not initialized");

        // v3.1.0 API: SystemMessage is a plain string property, no SystemMessage class
        var request = new MessageParameters
        {
            Model         = _settings.AiModel,
            MaxTokens     = 1024,
            SystemMessage = SystemPrompt,
            Messages      = new List<Message>
            {
                new Message { Role = RoleType.User, Content = userMessage }
            }
        };

        // v3.1.0 signature: GetClaudeMessageAsync(parameters, tools, cancellationToken)
        var response = await _anthropicClient.Messages.GetClaudeMessageAsync(request, null, ct);

        return response.Content
            .OfType<TextContent>()
            .FirstOrDefault()?.Text ?? "{}";
    }

    private static AIDocumentation ParseJsonResponse(string raw)
    {
        var cleaned = raw.Replace("```json", "").Replace("```", "").Trim();
        try
        {
            var opts = new JsonSerializerOptions { PropertyNameCaseInsensitive = true };
            var data = JsonSerializer.Deserialize<JsonElement>(cleaned, opts);
            return new AIDocumentation
            {
                Summary      = GetString(data, "summary"),
                Purpose      = GetString(data, "purpose"),
                KeyFunctions = GetStringList(data, "key_functions"),
                Inputs       = GetStringList(data, "inputs"),
                Outputs      = GetStringList(data, "outputs"),
                Dependencies = GetStringList(data, "dependencies"),
                Complexity   = GetString(data, "complexity", "Unknown"),
                Notes        = data.TryGetProperty("notes", out var n) ? n.GetString() : null
            };
        }
        catch
        {
            return new AIDocumentation
            {
                Summary = "Could not parse AI response",
                Notes   = raw[..Math.Min(300, raw.Length)]
            };
        }
    }

    private static string GetString(JsonElement el, string key, string fallback = "")
        => el.TryGetProperty(key, out var v) ? v.GetString() ?? fallback : fallback;

    private static List<string> GetStringList(JsonElement el, string key)
    {
        if (!el.TryGetProperty(key, out var arr) || arr.ValueKind != JsonValueKind.Array)
            return [];
        return arr.EnumerateArray()
                  .Select(e => e.GetString() ?? "")
                  .Where(s => s != "")
                  .ToList();
    }

    private static AIDocumentation StubDocumentation(string fileId, string filePath, string language)
        => new()
        {
            FileId       = fileId,
            FilePath     = filePath,
            Summary      = $"{Path.GetFileName(filePath)} — AI summary not available",
            Purpose      = "Add ANTHROPIC_API_KEY to appsettings.json to enable AI documentation.",
            KeyFunctions = [],
            Inputs       = [],
            Outputs      = [],
            Dependencies = [],
            Complexity   = "Unknown",
            Notes        = $"Language detected: {language}. AI features are optional."
        };
}
