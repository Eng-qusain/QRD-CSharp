using System.Text.Json;
using System.Text.RegularExpressions;
using QRD.Core.Infrastructure.PDF;
using QRD.Utils;

namespace QRD.Core.Infrastructure.Syntax;

/// <summary>
/// Manages user-defined language extension rules loaded from
/// appsettings.json under the "LanguageExtensions" key.
///
/// Example entry in appsettings.json:
/// {
///   "LanguageExtensions": [
///     {
///       "Name":               "Lua",
///       "Extensions":         ".lua",
///       "LineComment":        "--",
///       "Keywords":           "and,break,do,else,elseif,end,false,for,function,goto,if,in,local,nil,not,or,repeat,return,then,true,until,while",
///       "StringDelimiters":   "\",'",
///       "NumberPattern":      "\\b\\d+(\\.\\d+)?\\b",
///       "TypePattern":        "\\b[A-Z][A-Za-z0-9_]*\\b"
///     }
///   ]
/// }
/// Comma-separate multiple extensions: ".lua,.luac"
/// </summary>
public class LanguageExtensionManager
{
    private readonly List<UserLanguage> _languages = [];
    public IReadOnlyList<UserLanguage> Languages => _languages;

    public LanguageExtensionManager(AppSettings settings) => Reload();

    public void Reload()
    {
        _languages.Clear();
        var path = Path.Combine(AppContext.BaseDirectory, "appsettings.json");
        if (!File.Exists(path)) return;
        try
        {
            var doc = JsonDocument.Parse(File.ReadAllText(path));
            if (!doc.RootElement.TryGetProperty("LanguageExtensions", out var arr)) return;
            foreach (var el in arr.EnumerateArray())
            {
                var lang = new UserLanguage
                {
                    Name               = el.S("Name"),
                    Extensions         = el.S("Extensions").Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries),
                    LineComment        = el.S("LineComment"),
                    BlockCommentStart  = el.S("BlockCommentStart"),
                    BlockCommentEnd    = el.S("BlockCommentEnd"),
                    Keywords           = el.S("Keywords").Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries),
                    StringDelimiters   = el.S("StringDelimiters", "\",'"),
                    NumberPattern      = el.S("NumberPattern", @"\b\d+(\.\d+)?\b"),
                    TypePattern        = el.S("TypePattern"),
                    FunctionPattern    = el.S("FunctionPattern"),
                    PreprocessorPattern= el.S("PreprocessorPattern"),
                };
                if (!string.IsNullOrEmpty(lang.Name)) _languages.Add(lang);
            }
        }
        catch { /* silently ignore malformed entries */ }
    }

    public UserLanguage?    FindByExtension(string ext) =>
        _languages.FirstOrDefault(l => l.Extensions.Contains(ext, StringComparer.OrdinalIgnoreCase));

    public UserHighlighter? GetHighlighter(string ext)
    {
        var lang = FindByExtension(ext); return lang is null ? null : new UserHighlighter(lang);
    }
}

// ── JsonElement helper ────────────────────────────────────────────────────────

internal static class JExt
{
    public static string S(this JsonElement e, string key, string fallback = "")
        => e.TryGetProperty(key, out var v) ? v.GetString() ?? fallback : fallback;
}

// ── User-defined language data ────────────────────────────────────────────────

public class UserLanguage
{
    public string   Name                { get; set; } = "";
    public string[] Extensions          { get; set; } = [];
    public string   LineComment         { get; set; } = "";
    public string   BlockCommentStart   { get; set; } = "";
    public string   BlockCommentEnd     { get; set; } = "";
    public string[] Keywords            { get; set; } = [];
    public string   StringDelimiters    { get; set; } = "\",'";
    public string   NumberPattern       { get; set; } = @"\b\d+(\.\d+)?\b";
    public string   TypePattern         { get; set; } = "";
    public string   FunctionPattern     { get; set; } = "";
    public string   PreprocessorPattern { get; set; } = "";
}

// ── Runtime highlighter built from a UserLanguage ─────────────────────────────

public class UserHighlighter : SyntaxHighlighter
{
    private readonly (Regex Pattern, TokenKind Kind)[] _rules;

    public UserHighlighter(UserLanguage lang)
    {
        var rules = new List<(Regex, TokenKind)>();

        if (!string.IsNullOrEmpty(lang.BlockCommentStart) && !string.IsNullOrEmpty(lang.BlockCommentEnd))
            rules.Add((Safe($"{Esc(lang.BlockCommentStart)}.*?{Esc(lang.BlockCommentEnd)}"), TokenKind.Comment));

        if (!string.IsNullOrEmpty(lang.LineComment))
            rules.Add((Safe($"{Esc(lang.LineComment)}.*$"), TokenKind.Comment));

        foreach (var d in lang.StringDelimiters)
            rules.Add((Safe($"{Esc(d.ToString())}[^{Esc(d.ToString())}\\n]*{Esc(d.ToString())}"), TokenKind.String));

        if (lang.Keywords.Length > 0)
            rules.Add((Safe($@"\b({string.Join("|", lang.Keywords.Select(Esc))})\b"), TokenKind.Keyword));

        if (!string.IsNullOrEmpty(lang.FunctionPattern))    rules.Add((Safe(lang.FunctionPattern),     TokenKind.Function));
        if (!string.IsNullOrEmpty(lang.TypePattern))        rules.Add((Safe(lang.TypePattern),          TokenKind.Type));
        if (!string.IsNullOrEmpty(lang.NumberPattern))      rules.Add((Safe(lang.NumberPattern),        TokenKind.Number));
        if (!string.IsNullOrEmpty(lang.PreprocessorPattern))rules.Add((Safe(lang.PreprocessorPattern),  TokenKind.Preprocessor));

        _rules = [.. rules];
    }

    private static Regex  Safe(string p) { try { return new Regex(p); } catch { return new Regex(@"(?!x)x"); } }
    private static string Esc(string s)  => Regex.Escape(s);

    public override List<(string, TokenKind)> Tokenize(string line) => Lex(line, _rules);
}
