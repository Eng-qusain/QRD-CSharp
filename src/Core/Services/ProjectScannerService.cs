using System.Text.RegularExpressions;
using QRD.Core.Domain.Entities;
using FileInfo = QRD.Core.Domain.Entities.FileInfo;

namespace QRD.Core.Services;

/// <summary>
/// Recursively scans a project directory, classifies files, counts lines,
/// and builds a directory tree.  Equivalent to Python's ProjectScannerService.
/// Supports 100 K+ file repositories via chunked parallel scanning with progress callbacks.
/// </summary>
public class ProjectScannerService
{
    private const int DefaultChunkSize = 500;
    private const long MaxFileSizeBytes = 50L * 1024 * 1024;

    // ── Extension Maps ───────────────────────────────────────────────────────

    private static readonly Dictionary<string, Language> ExtToLanguage = new(StringComparer.OrdinalIgnoreCase)
    {
        [".py"]   = Language.Python,
        [".js"]   = Language.JavaScript,
        [".ts"]   = Language.TypeScript,
        [".tsx"]  = Language.ReactTsx,
        [".jsx"]  = Language.JavaScript,
        [".sh"]   = Language.Shell,
        [".bash"] = Language.Bash,
        [".zsh"]  = Language.Shell,
        [".yaml"] = Language.Yaml,
        [".yml"]  = Language.Yaml,
        [".json"] = Language.Json,
        [".toml"] = Language.Toml,
        [".ini"]  = Language.Ini,
        [".cfg"]  = Language.Ini,
        [".html"] = Language.Html,
        [".htm"]  = Language.Html,
        [".css"]  = Language.Css,
        [".scss"] = Language.Css,
        [".sass"] = Language.Css,
        [".sql"]  = Language.Sql,
        [".md"]   = Language.Markdown,
        [".rst"]  = Language.Markdown,
        [".xml"]  = Language.Xml,
        [".cs"]   = Language.CSharp,
        [".cpp"]  = Language.Cpp,
        [".h"]    = Language.Cpp,
        [".java"] = Language.Java,
        [".go"]   = Language.Go,
        [".rs"]   = Language.Rust,
    };

    private static readonly Dictionary<string, FileCategory> ExtToCategory = new(StringComparer.OrdinalIgnoreCase)
    {
        [".py"]    = FileCategory.Source,
        [".js"]    = FileCategory.Source,
        [".ts"]    = FileCategory.Source,
        [".tsx"]   = FileCategory.Source,
        [".jsx"]   = FileCategory.Source,
        [".sh"]    = FileCategory.Source,
        [".bash"]  = FileCategory.Source,
        [".zsh"]   = FileCategory.Source,
        [".html"]  = FileCategory.Source,
        [".css"]   = FileCategory.Source,
        [".scss"]  = FileCategory.Source,
        [".sql"]   = FileCategory.Source,
        [".cs"]    = FileCategory.Source,
        [".cpp"]   = FileCategory.Source,
        [".h"]     = FileCategory.Source,
        [".java"]  = FileCategory.Source,
        [".go"]    = FileCategory.Source,
        [".rs"]    = FileCategory.Source,
        [".yaml"]  = FileCategory.Config,
        [".yml"]   = FileCategory.Config,
        [".json"]  = FileCategory.Config,
        [".toml"]  = FileCategory.Config,
        [".ini"]   = FileCategory.Config,
        [".cfg"]   = FileCategory.Config,
        [".xml"]   = FileCategory.Config,
        [".md"]    = FileCategory.Document,
        [".rst"]   = FileCategory.Document,
        [".txt"]   = FileCategory.Document,
        [".pdf"]   = FileCategory.Document,
        [".docx"]  = FileCategory.Document,
        [".csv"]   = FileCategory.Data,
        [".xlsx"]  = FileCategory.Data,
        [".xls"]   = FileCategory.Data,
        [".parquet"] = FileCategory.Data,
        [".svg"]   = FileCategory.Visual,
        [".png"]   = FileCategory.Visual,
        [".jpg"]   = FileCategory.Visual,
        [".jpeg"]  = FileCategory.Visual,
        [".webp"]  = FileCategory.Visual,
        [".las"]   = FileCategory.Petroleum,
        [".dlis"]  = FileCategory.Petroleum,
        [".lis"]   = FileCategory.Petroleum,
    };

    private static readonly HashSet<string> BinaryExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".png", ".jpg", ".jpeg", ".webp", ".pdf", ".docx", ".xlsx", ".xls",
        ".parquet", ".dlis", ".lis", ".gif", ".ico", ".woff", ".woff2", ".ttf",
        ".eot", ".otf", ".zip", ".tar", ".gz", ".exe", ".dll"
    };

    private static readonly string[] DefaultExcludePatterns =
    [
        "__pycache__", "*.pyc", "*.pyo", "*.pyd",
        "node_modules", ".npm",
        ".git", ".svn", ".hg",
        ".venv", "venv", "env",
        "dist", "build", ".next", ".nuxt",
        "coverage", ".nyc_output",
        ".pytest_cache", ".mypy_cache", ".ruff_cache",
        "bin", "obj",                        // .NET build outputs
        "*.egg-info", "*.egg",
        ".DS_Store", "Thumbs.db",
    ];

    // ── Public API ────────────────────────────────────────────────────────────

    public async Task<ProjectScan> ScanAsync(
        string projectPath,
        IEnumerable<string>? excludePatterns = null,
        IProgress<(double Percent, string Message)>? progress = null,
        CancellationToken ct = default)
    {
        var start = DateTime.UtcNow;
        var root = Path.GetFullPath(projectPath);

        if (!Directory.Exists(root))
            throw new DirectoryNotFoundException($"Project path does not exist: {root}");

        var patterns = DefaultExcludePatterns.Concat(excludePatterns ?? []).ToList();

        // Phase 1 — collect paths
        progress?.Report((5, "Collecting file list…"));
        var allPaths = await Task.Run(() => WalkDirectory(root, patterns, ct), ct);
        var total = allPaths.Count;
        progress?.Report((8, $"Found {total} files"));

        // Phase 2 — process in chunks
        var flatFiles = new List<FileInfo>(total);
        var processed = 0;

        for (var i = 0; i < total; i += DefaultChunkSize)
        {
            ct.ThrowIfCancellationRequested();
            var chunk = allPaths.Skip(i).Take(DefaultChunkSize).ToList();
            var results = await Task.WhenAll(chunk.Select(p => Task.Run(() => ProcessFile(p, root), ct)));
            flatFiles.AddRange(results.Where(r => r is not null)!);
            processed += chunk.Count;
            var pct = 8 + processed / (double)total * 82;
            progress?.Report((pct, $"Processed {processed}/{total}"));
        }

        // Phase 3 — build tree
        progress?.Report((92, "Building file tree…"));
        var tree = await Task.Run(() => BuildTree(root, flatFiles), ct);

        // Phase 4 — stats
        progress?.Report((97, "Computing statistics…"));
        var stats = ComputeStats(flatFiles);

        var durationMs = (DateTime.UtcNow - start).TotalMilliseconds;
        progress?.Report((100, "Scan complete"));

        return new ProjectScan
        {
            ProjectPath = root,
            ProjectName = Path.GetFileName(root),
            ScannedAt = DateTime.UtcNow,
            FileTree = tree,
            FlatFiles = flatFiles,
            Stats = stats,
            ExcludePatterns = patterns,
            ScanDurationMs = durationMs
        };
    }

    // ── Private helpers ───────────────────────────────────────────────────────

    private static List<string> WalkDirectory(string root, List<string> patterns, CancellationToken ct)
    {
        var result = new List<string>();
        var stack = new Stack<string>();
        stack.Push(root);

        while (stack.Count > 0)
        {
            ct.ThrowIfCancellationRequested();
            var dir = stack.Pop();
            var relDir = Path.GetRelativePath(root, dir);

            try
            {
                var subDirs = Directory.GetDirectories(dir)
                    .Where(d => !ShouldExclude(Path.GetFileName(d), Path.GetRelativePath(root, d), patterns))
                    .OrderBy(d => d);

                foreach (var sub in subDirs) stack.Push(sub);

                var files = Directory.GetFiles(dir)
                    .Where(f => !ShouldExclude(Path.GetFileName(f), Path.GetRelativePath(root, f), patterns))
                    .OrderBy(f => f);

                result.AddRange(files);
            }
            catch (UnauthorizedAccessException) { /* skip inaccessible dirs */ }
        }

        return result;
    }

    private static bool ShouldExclude(string name, string relPath, List<string> patterns)
    {
        foreach (var pattern in patterns)
        {
            if (MatchGlob(name, pattern)) return true;
            if (MatchGlob(relPath.Replace('\\', '/'), pattern)) return true;
        }
        return false;
    }

    private static bool MatchGlob(string input, string pattern)
    {
        // Convert glob to regex
        var regexPattern = "^" + Regex.Escape(pattern).Replace(@"\*", ".*").Replace(@"\?", ".") + "$";
        return Regex.IsMatch(input, regexPattern, RegexOptions.IgnoreCase);
    }

    private static FileInfo? ProcessFile(string path, string root)
    {
        try
        {
            var info = new System.IO.FileInfo(path);
            var ext = Path.GetExtension(path).ToLowerInvariant();
            var rel = Path.GetRelativePath(root, path).Replace('\\', '/');

            var isBinary = BinaryExtensions.Contains(ext);
            var category = ExtToCategory.GetValueOrDefault(ext, FileCategory.Unknown);
            var language = ExtToLanguage.GetValueOrDefault(ext);
            int? lineCount = null;

            if (!isBinary && info.Length < MaxFileSizeBytes)
            {
                try
                {
                    var bytes = File.ReadAllBytes(path);
                    // Detect binary by null bytes in first 8 KB
                    if (bytes.Take(8192).Contains((byte)0))
                    {
                        isBinary = true;
                    }
                    else
                    {
                        var text = System.Text.Encoding.UTF8.GetString(bytes);
                        lineCount = text.Count(c => c == '\n') + (text.Length > 0 && text[^1] != '\n' ? 1 : 0);
                    }
                }
                catch { isBinary = true; }
            }

            return new FileInfo(rel)
            {
                Name = Path.GetFileName(path),
                Path = path,
                RelativePath = rel,
                Extension = ext,
                SizeBytes = info.Length,
                LastModified = info.LastWriteTimeUtc,
                Category = category,
                Language = language,
                LineCount = lineCount,
                IsBinary = isBinary
            };
        }
        catch
        {
            return null;
        }
    }

    private static DirectoryNode BuildTree(string root, List<FileInfo> files)
    {
        var dirFiles = files.GroupBy(f => Path.GetDirectoryName(f.RelativePath)?.Replace('\\', '/') ?? "")
                            .ToDictionary(g => g.Key, g => g.ToList());

        var allDirs = new HashSet<string> { "" };
        foreach (var f in files)
        {
            var parts = f.RelativePath.Split('/');
            for (var i = 1; i < parts.Length; i++)
                allDirs.Add(string.Join("/", parts[..i]));
        }

        DirectoryNode BuildNode(string relPath, int depth)
        {
            var absPath = relPath == "" ? root : Path.Combine(root, relPath);
            var node = new DirectoryNode
            {
                Name = relPath == "" ? Path.GetFileName(root) : Path.GetFileName(relPath),
                Path = absPath,
                RelativePath = relPath == "" ? "." : relPath,
                Files = dirFiles.GetValueOrDefault(relPath, []),
                Depth = depth
            };

            var children = allDirs
                .Where(d => d != "" && d != relPath &&
                            (relPath == ""
                                ? !d.Contains('/')
                                : d.StartsWith(relPath + "/") && d[(relPath.Length + 1)..].IndexOf('/') == -1))
                .OrderBy(d => d)
                .Select(d => BuildNode(d, depth + 1))
                .ToList();

            node.ChildrenDirs.AddRange(children);
            return node;
        }

        return BuildNode("", 0);
    }

    private static ProjectStats ComputeStats(List<FileInfo> files)
    {
        var langDist = new Dictionary<string, int>();
        var extDist = new Dictionary<string, int>();
        var catDist = new Dictionary<string, int>();

        long totalLines = 0, totalSize = 0;

        foreach (var f in files)
        {
            var lang = f.Language?.ToString() ?? "Unknown";
            langDist[lang] = langDist.GetValueOrDefault(lang) + 1;

            var ext = string.IsNullOrEmpty(f.Extension) ? "(no ext)" : f.Extension;
            extDist[ext] = extDist.GetValueOrDefault(ext) + 1;

            var cat = f.Category.ToString();
            catDist[cat] = catDist.GetValueOrDefault(cat) + 1;

            totalSize += f.SizeBytes;
            totalLines += f.LineCount ?? 0;
        }

        var largest = files
            .OrderByDescending(f => f.SizeBytes)
            .Take(20)
            .Select(f => new LargestFileEntry(
                f.RelativePath, f.SizeBytes, f.LineCount ?? 0,
                f.Language?.ToString() ?? "Unknown"))
            .ToList();

        var totalDirs = files.Select(f => Path.GetDirectoryName(f.RelativePath)).Distinct().Count();

        return new ProjectStats
        {
            TotalFiles = files.Count,
            TotalDirectories = totalDirs,
            TotalLines = totalLines,
            TotalSize = totalSize,
            LanguageDistribution = langDist,
            ExtensionDistribution = extDist,
            CategoryDistribution = catDist,
            LargestFiles = largest,
            AverageFileSize = files.Count > 0 ? totalSize / (double)files.Count : 0,
            AverageLineCount = files.Count > 0 ? totalLines / (double)files.Count : 0
        };
    }
}
