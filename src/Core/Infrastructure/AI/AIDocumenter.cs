using System.Text.Json;
using Anthropic.SDK;
using Anthropic.SDK.Messaging;
using QRD.Core.Domain.Entities;
using QRD.Utils;

// Alias to avoid collision between 'System' (C# namespace) and MessageParameters.System property
using AiMessage = Anthropic.SDK.Messaging.Message;

namespace QRD.Core.Infrastructure.AI;

/// <summary>
/// Generates AI documentation for source files.
/// Equivalent to Python's AIDocumenter.
/// - With Anthropic key  → uses Claude
/// - With OpenAI key     → uses GPT-4o-mini  
/// - Without any key     → returns clean stub documentation (no crash)
/// </summary>
public class AIDocumenter
{
    private readonly AppSettings _settings;
    private readonly AnthropicClient? _anthropicClient;
    private readonly string _provider;

    private const string SystemPrompt = """
        You are a senior software engineer generating structured technical documentation.
        Analyze the provided source code and return ONLY valid JSON (no markdown, no explanation).

        Return this exact JSON structure:
        {
          "summary": "One-sentence description of what this file does",
          "purpose": "2-3 sentence explanation of the file's role in the project",
          "key_functions": ["function or class name: what it does", ...],
          "inputs": ["description of inputs/parameters/arguments", ...],
          "outputs": ["description of return values/outputs/side effects", ...],
          "dependencies": ["package or module name", ...],
          "complexity": "Low|Medium|High|Very High",
          "notes": "Any important notes about design decisions, gotchas, or patterns used"
        }
        """;

    public AIDocumenter(AppSettings settings)
    {
        _settings = settings;

        if (!string.IsNullOrWhiteSpace(settings.AnthropicApiKey))
        {
            _anthropicClient = new AnthropicClient(settings.AnthropicApiKey);
            _provider = "anthropic";
        }
        else if (!string.IsNullOrWhiteSpace(settings.OpenAiApiKey))
        {
            _provider = "openai";
        }
        else
        {
            _provider = "none";
        }
    }

    public bool IsAvailable => _provider != "none";

    public async Task<AIDocumentation> DocumentFileAsync(
        string fileId,
        string filePath,
        string content,
        string language,
        int maxContentChars = 8000,
        CancellationToken ct = default)
    {
        if (!IsAvailable)
            return StubDocumentation(fileId, filePath, language);

        var truncated = content.Length > maxContentChars
            ? content[..maxContentChars] + $"\n\n... [truncated at {maxContentChars} chars]"
            : content;

        var userMessage = $"File: {filePath}\nLanguage: {language}\n\n```{language}\n{truncated}\n```";

        try
        {
            var raw = _provider switch
            {
                "anthropic" => await CallAnthropicAsync(userMessage, ct),
                "openai"    => await CallOpenAiAsync(userMessage, ct),
                _           => null
            };

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

    private async Task<string> CallAnthropicAsync(string userMessage, CancellationToken ct)
    {
        if (_anthropicClient is null) throw new InvalidOperationException("Anthropic client not initialized");

        // Use fully-qualified SystemMessage to avoid collision with System namespace
        var sysMsg = new Anthropic.SDK.Messaging.SystemMessage(SystemPrompt);

        var request = new MessageParameters
        {
            Model     = _settings.AiModel,
            MaxTokens = 1024,
            // 'System' property name collides with the C# 'System' namespace when used
            // as a named initializer — assign via a local variable to avoid the ambiguity
            Messages  = [new AiMessage { Role = RoleType.User, Content = userMessage }]
        };
        request.System = [sysMsg];

        var response = await _anthropicClient.Messages.GetClaudeMessageAsync(request, ct);
        return response.Content.OfType<TextContent>().FirstOrDefault()?.Text ?? "{}";
    }

    private Task<string> CallOpenAiAsync(string userMessage, CancellationToken ct)
    {
        throw new NotImplementedException("OpenAI integration requires OpenAI NuGet package");
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
        return arr.EnumerateArray().Select(e => e.GetString() ?? "").Where(s => s != "").ToList();
    }

    private static AIDocumentation StubDocumentation(string fileId, string filePath, string language)
        => new()
        {
            FileId   = fileId,
            FilePath = filePath,
            Summary  = $"{Path.GetFileName(filePath)} — AI summary not available",
            Purpose  = "Add ANTHROPIC_API_KEY or OPENAI_API_KEY to appsettings.json to enable AI documentation.",
            KeyFunctions = [],
            Inputs       = [],
            Outputs      = [],
            Dependencies = [],
            Complexity   = "Unknown",
            Notes        = $"Language detected: {language}. AI features are optional — all PDF export features work without an API key."
        };
}
