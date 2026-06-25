using QRD.Utils;

namespace QRD.Core.Infrastructure.Storage;

/// <summary>
/// Manages the temporary directory for export jobs.
/// Equivalent to Python's TempManager.
/// </summary>
public class TempManager(AppSettings settings)
{
    public string TempDir => settings.TempDir;

    public void Initialize()
    {
        Directory.CreateDirectory(settings.TempDir);
        Directory.CreateDirectory(settings.OutputDir);
    }

    public string CreateJobDirectory(string jobId)
    {
        var dir = Path.Combine(settings.TempDir, jobId);
        Directory.CreateDirectory(dir);
        return dir;
    }

    public void CleanupJobDirectory(string jobId)
    {
        var dir = Path.Combine(settings.TempDir, jobId);
        if (Directory.Exists(dir))
            Directory.Delete(dir, recursive: true);
    }

    /// <summary>Remove temp directories older than 24 hours.</summary>
    public void CleanupOldTempFiles(TimeSpan maxAge)
    {
        if (!Directory.Exists(settings.TempDir)) return;

        foreach (var dir in Directory.GetDirectories(settings.TempDir))
        {
            var info = new DirectoryInfo(dir);
            if (DateTime.UtcNow - info.CreationTimeUtc > maxAge)
            {
                try { Directory.Delete(dir, recursive: true); }
                catch { /* ignore locked dirs */ }
            }
        }
    }
}
