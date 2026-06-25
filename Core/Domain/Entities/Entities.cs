using System.Security.Cryptography;
using System.Text;

namespace QRD.Core.Domain.Entities;

// ── Value Objects ────────────────────────────────────────────────────────────

public enum FileCategory
{
    Source,
    Data,
    Visual,
    Petroleum,
    Document,
    Config,
    Unknown
}

public enum Language
{
    Python,
    JavaScript,
    TypeScript,
    ReactTsx,
    Shell,
    Bash,
    Yaml,
    Json,
    Toml,
    Ini,
    Html,
    Css,
    Sql,
    Markdown,
    Csv,
    Xml,
    CSharp,
    Cpp,
    Java,
    Go,
    Rust,
    Unknown
}

public enum ExportMode
{
    Single,     // One combined PDF
    Folder,     // One PDF per top-level folder
    File,       // One PDF per file
    Package     // Full documentation package
}

// ── Entities ─────────────────────────────────────────────────────────────────

public class FileInfo
{
    public string Id { get; init; }
    public string Name { get; init; } = "";
    public string Path { get; init; } = "";
    public string RelativePath { get; init; } = "";
    public string Extension { get; init; } = "";
    public long SizeBytes { get; init; }
    public DateTime LastModified { get; init; }
    public FileCategory Category { get; init; }
    public Language? Language { get; init; }
    public int? LineCount { get; init; }
    public string Encoding { get; init; } = "utf-8";
    public bool IsBinary { get; init; }

    public double SizeKb => SizeBytes / 1024.0;
    public double SizeMb => SizeBytes / (1024.0 * 1024.0);
    public bool IsSourceFile => Category == FileCategory.Source;
    public bool IsLarge => SizeBytes > 10 * 1024 * 1024;

    public FileInfo(string relativePath)
    {
        Id = MD5Hash(relativePath)[..12];
    }

    private static string MD5Hash(string input)
    {
        var bytes = MD5.HashData(System.Text.Encoding.UTF8.GetBytes(input));
        return Convert.ToHexString(bytes).ToLower();
    }
}

public class DirectoryNode
{
    public string Name { get; init; } = "";
    public string Path { get; init; } = "";
    public string RelativePath { get; init; } = "";
    public List<DirectoryNode> ChildrenDirs { get; init; } = [];
    public List<FileInfo> Files { get; init; } = [];
    public int Depth { get; init; }

    public int TotalFiles =>
        Files.Count + ChildrenDirs.Sum(d => d.TotalFiles);

    public long TotalSize =>
        Files.Sum(f => f.SizeBytes) + ChildrenDirs.Sum(d => d.TotalSize);
}

public class ProjectScan
{
    public string ProjectPath { get; init; } = "";
    public string ProjectName { get; init; } = "";
    public DateTime ScannedAt { get; init; }
    public DirectoryNode FileTree { get; init; } = new();
    public List<FileInfo> FlatFiles { get; init; } = [];
    public ProjectStats Stats { get; init; } = new();
    public List<string> ExcludePatterns { get; init; } = [];
    public double ScanDurationMs { get; init; }
}

public class ProjectStats
{
    public int TotalFiles { get; init; }
    public int TotalDirectories { get; init; }
    public long TotalLines { get; init; }
    public long TotalSize { get; init; }
    public Dictionary<string, int> LanguageDistribution { get; init; } = [];
    public Dictionary<string, int> ExtensionDistribution { get; init; } = [];
    public Dictionary<string, int> CategoryDistribution { get; init; } = [];
    public List<LargestFileEntry> LargestFiles { get; init; } = [];
    public double AverageFileSize { get; init; }
    public double AverageLineCount { get; init; }
}

public record LargestFileEntry(string Path, long Size, int Lines, string Language);

public class AIDocumentation
{
    public string FileId { get; set; } = "";
    public string FilePath { get; set; } = "";
    public string Summary { get; set; } = "";
    public string Purpose { get; set; } = "";
    public List<string> KeyFunctions { get; set; } = [];
    public List<string> Inputs { get; set; } = [];
    public List<string> Outputs { get; set; } = [];
    public List<string> Dependencies { get; set; } = [];
    public string Complexity { get; set; } = "Unknown";
    public string? Notes { get; set; }
    public DateTime? GeneratedAt { get; set; }
    public string? ModelUsed { get; set; }
}

public class ExportJob
{
    public string Id { get; init; } = Guid.NewGuid().ToString();
    public string ProjectPath { get; init; } = "";
    public ExportMode Mode { get; init; }
    public string OutputPath { get; init; } = "";
    public string Status { get; set; } = "pending";
    public double Progress { get; set; }
    public string CurrentFile { get; set; } = "";
    public int TotalFiles { get; set; }
    public int ProcessedFiles { get; set; }
    public List<string> OutputFiles { get; set; } = [];
    public string? Error { get; set; }
    public DateTime? StartedAt { get; set; }
    public DateTime? CompletedAt { get; set; }

    public double? DurationSeconds =>
        StartedAt.HasValue && CompletedAt.HasValue
            ? (CompletedAt.Value - StartedAt.Value).TotalSeconds
            : null;

    public double? EstimatedRemainingSeconds
    {
        get
        {
            if (!StartedAt.HasValue || Progress <= 0) return null;
            var elapsed = (DateTime.UtcNow - StartedAt.Value).TotalSeconds;
            if (Progress >= 100) return 0.0;
            return elapsed * (100 - Progress) / Progress;
        }
    }
}

public class PetroleumWellData
{
    public string WellName { get; init; } = "";
    public string? Field { get; init; }
    public string? Location { get; init; }
    public string? Country { get; init; }
    public string FilePath { get; init; } = "";
    public string FileFormat { get; init; } = "";
    public List<Dictionary<string, object>> Curves { get; init; } = [];
    public Dictionary<string, string> Header { get; init; } = [];
    public (double Min, double Max)? DataDepthRange { get; init; }
    public int CurveCount { get; init; }
    public int SampleCount { get; init; }
}
