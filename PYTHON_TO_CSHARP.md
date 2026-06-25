# Python → C# Side-by-Side Translation Guide

This document shows the **exact same logic** written in both Python (the original)
and C# (this rebuild), so you can read either version and understand the other.

---

## 1. Settings / Configuration

### Python (`utils/config.py`)
```python
from pydantic_settings import BaseSettings, SettingsConfigDict
from pydantic import Field

class Settings(BaseSettings):
    model_config = SettingsConfigDict(env_file=".env")
    
    host: str = Field(default="127.0.0.1", validation_alias="REPODOC_HOST")
    port: int = Field(default=8765, validation_alias="REPODOC_PORT")
    anthropic_api_key: Optional[str] = Field(default=None, validation_alias="ANTHROPIC_API_KEY")
    
    @property
    def is_development(self) -> bool:
        return self.env == "development"
    
    @property
    def max_file_size_bytes(self) -> int:
        return self.max_file_size_mb * 1024 * 1024

settings = Settings()
```

### C# (`Utils/AppSettings.cs`)
```csharp
public class AppSettings
{
    public string Host { get; set; } = "127.0.0.1";
    public int Port { get; set; } = 8765;
    public string? AnthropicApiKey { get; set; }
    
    // C# computed property = Python @property
    public bool IsDevelopment => Env == "development";
    public long MaxFileSizeBytes => (long)MaxFileSizeMb * 1024 * 1024;
    
    public static AppSettings Load()
    {
        var config = new ConfigurationBuilder()
            .AddJsonFile("appsettings.json", optional: true)
            .AddEnvironmentVariables(prefix: "QRD_")
            .Build();
        var settings = new AppSettings();
        config.Bind(settings);
        settings.AnthropicApiKey ??= Environment.GetEnvironmentVariable("ANTHROPIC_API_KEY");
        return settings;
    }
}
```

**Key differences:**
- Python uses `pydantic` magic. C# uses `IConfiguration` (Microsoft's built-in config system).
- Python `Optional[str]` = C# `string?` (the `?` means nullable).
- Python `@property` = C# `=> expression` (arrow property).
- Python reads `.env`. C# reads `appsettings.json` + environment variables.

---

## 2. Domain Entities (Data Classes)

### Python (`core/domain/entities/entities.py`)
```python
from dataclasses import dataclass, field
from enum import Enum
from pathlib import Path
from typing import Optional

class FileCategory(str, Enum):
    SOURCE = "source"
    DATA = "data"

@dataclass
class FileInfo:
    name: str
    path: Path
    relative_path: str
    extension: str
    size_bytes: int
    category: FileCategory
    line_count: Optional[int] = None
    is_binary: bool = False

    @property
    def size_kb(self) -> float:
        return self.size_bytes / 1024
```

### C# (`Core/Domain/Entities/Entities.cs`)
```csharp
public enum FileCategory
{
    Source,
    Data,
}

public class FileInfo
{
    public string Name { get; init; } = "";
    public string Path { get; init; } = "";
    public string RelativePath { get; init; } = "";
    public string Extension { get; init; } = "";
    public long SizeBytes { get; init; }
    public FileCategory Category { get; init; }
    public int? LineCount { get; init; }     // int? = Optional[int]
    public bool IsBinary { get; init; }

    // @property equivalent
    public double SizeKb => SizeBytes / 1024.0;
}
```

**Key differences:**
- Python `@dataclass` → C# class with `{ get; init; }` properties
- Python `Optional[int]` → C# `int?` (null means "not set")
- Python `Path` type → C# `string` (paths are just strings in C#)
- `init` means "can only be set at creation time" — like Python `frozen=True`

---

## 3. Async File Scanning

### Python (`core/services/scanner_service.py`)
```python
import asyncio
import os

class ProjectScannerService:
    async def scan(self, project_path: str, ...) -> ProjectScan:
        start_time = time.time()
        root = Path(project_path).resolve()
        
        # Phase 1: collect paths (blocking → run in thread)
        all_paths = await asyncio.to_thread(
            self._walk_directory, root, patterns, cancel_event
        )
        
        # Phase 2: process in chunks
        for chunk_start in range(0, total, self._chunk_size):
            chunk = all_paths[chunk_start : chunk_start + self._chunk_size]
            results = await asyncio.gather(
                *[self._process_file(p, root) for p in chunk]
            )
            flat_files.extend(r for r in results if isinstance(r, FileInfo))
            
            await asyncio.sleep(0)  # yield to event loop
        
        return ProjectScan(...)
    
    def _walk_directory(self, root, patterns, cancel_event):
        result = []
        for dirpath, dirnames, filenames in os.walk(root):
            # filter + collect
            ...
        return result
```

### C# (`Core/Services/ProjectScannerService.cs`)
```csharp
public class ProjectScannerService
{
    public async Task<ProjectScan> ScanAsync(string projectPath, ...)
    {
        var start = DateTime.UtcNow;
        var root = Path.GetFullPath(projectPath);
        
        // Phase 1: collect paths (blocking → run in thread pool)
        var allPaths = await Task.Run(              // ← asyncio.to_thread()
            () => WalkDirectory(root, patterns, ct), ct);
        
        // Phase 2: process in chunks
        for (var i = 0; i < total; i += DefaultChunkSize)
        {
            var chunk = allPaths.Skip(i).Take(DefaultChunkSize).ToList();
            var results = await Task.WhenAll(      // ← asyncio.gather()
                chunk.Select(p => Task.Run(() => ProcessFile(p, root), ct)));
            flatFiles.AddRange(results.Where(r => r is not null)!);
        }
        
        return new ProjectScan { ... };
    }
    
    private static List<string> WalkDirectory(string root, ...)
    {
        var result = new List<string>();
        var stack = new Stack<string>();
        stack.Push(root);
        while (stack.Count > 0)
        {
            var dir = stack.Pop();
            foreach (var sub in Directory.GetDirectories(dir))
                stack.Push(sub);
            result.AddRange(Directory.GetFiles(dir));
        }
        return result;
    }
}
```

**Key differences:**
- `asyncio.to_thread(fn)` → `Task.Run(() => fn())`
- `asyncio.gather(*coros)` → `Task.WhenAll(tasks)`
- `asyncio.sleep(0)` → not needed in C# (thread pool manages this)
- Python `os.walk()` → C# iterative stack-based walk (no built-in recursive walk equivalent)

---

## 4. HTTP Routes (API Endpoints)

### Python (FastAPI — `api/routes/scanner.py`)
```python
from fastapi import APIRouter, HTTPException
from pydantic import BaseModel

router = APIRouter()

class ScanRequest(BaseModel):
    path: str
    exclude_patterns: list[str] = []

@router.post("/scan")
async def scan_project(request: ScanRequest) -> dict:
    try:
        result = await _scanner.scan(project_path=request.path)
    except FileNotFoundError as e:
        raise HTTPException(status_code=404, detail=str(e))
    except Exception as e:
        raise HTTPException(status_code=500, detail=str(e))
    
    return {
        "project_name": result.project_name,
        "total_files": result.stats.total_files,
    }
```

### C# (ASP.NET Core — `Api/Controllers/ScannerController.cs`)
```csharp
[ApiController]
[Route("scanner")]
public class ScannerController(ProjectScannerService scanner) : ControllerBase
{
    public record ScanRequest(string Path, List<string>? ExcludePatterns = null);

    [HttpPost("scan")]
    public async Task<IActionResult> ScanProject([FromBody] ScanRequest request, CancellationToken ct)
    {
        try
        {
            var result = await scanner.ScanAsync(request.Path, request.ExcludePatterns, ct: ct);
            return Ok(new {
                project_name = result.ProjectName,
                total_files  = result.Stats.TotalFiles,
            });
        }
        catch (DirectoryNotFoundException ex)
        {
            return NotFound(new { detail = ex.Message });   // 404
        }
        catch (Exception ex)
        {
            return StatusCode(500, new { detail = ex.Message });
        }
    }
}
```

**Key differences:**
- Python `@router.post("/scan")` → C# `[HttpPost("scan")]` attribute
- Python `raise HTTPException(404)` → C# `return NotFound(...)`
- Python `BaseModel` request → C# `record` (both validate incoming JSON automatically)
- Python FastAPI injects dependencies via function params. C# injects via constructor.
- `[FromBody]` tells C# to read the request body as JSON (FastAPI does this automatically)

---

## 5. AI Documentation

### Python (`core/infrastructure/ai/ai_documenter.py`)
```python
class AIDocumenter:
    def __init__(self, settings) -> None:
        if settings.anthropic_api_key:
            import anthropic
            self._client = anthropic.AsyncAnthropic(api_key=settings.anthropic_api_key)
            self._provider = "anthropic"
        else:
            self._provider = "none"

    @property
    def is_available(self) -> bool:
        return self._provider != "none"

    async def document_file(self, file_path, content, language) -> dict:
        if not self.is_available:
            return self._stub_documentation(file_path, language)
        
        response = await self._client.messages.create(
            model=self._settings.ai_model,
            max_tokens=1024,
            system=SYSTEM_PROMPT,
            messages=[{"role": "user", "content": user_message}],
        )
        return self._parse_json_response(response.content[0].text)
```

### C# (`Core/Infrastructure/AI/AIDocumenter.cs`)
```csharp
public class AIDocumenter(AppSettings settings)
{
    private readonly AnthropicClient? _client = 
        string.IsNullOrEmpty(settings.AnthropicApiKey)
            ? null
            : new AnthropicClient(settings.AnthropicApiKey);
    
    // @property equivalent
    public bool IsAvailable => _client is not null;
    
    public async Task<AIDocumentation> DocumentFileAsync(string fileId, string filePath, 
        string content, string language, CancellationToken ct = default)
    {
        if (!IsAvailable)
            return StubDocumentation(fileId, filePath, language);
        
        var request = new MessageParameters
        {
            Model    = settings.AiModel,
            MaxTokens = 1024,
            System   = [new SystemMessage(SystemPrompt)],
            Messages = [new Message { Role = RoleType.User, Content = userMessage }]
        };
        
        var response = await _client!.Messages.GetClaudeMessageAsync(request, ct);
        return ParseJsonResponse(response.Content.OfType<TextContent>().First().Text);
    }
}
```

**Key differences:**
- Python imports the library lazily inside `__init__` to handle "library not installed" case.
  C# uses nullable (`AnthropicClient?`) — if no key, client is `null`.
- Python `async def` → C# `async Task<T>`
- Both use the same JSON structure for the AI response — the parsing logic is almost identical.

---

## 6. Progress Reporting

### Python
```python
async def scan(self, ..., progress_callback=None, ...):
    if progress_callback:
        progress_callback(5.0, "Found N files")
    ...
    if progress_callback:
        progress_callback(100.0, "Scan complete")
```

### C#
```csharp
// IProgress<T> is the standard C# pattern for progress reporting
public async Task<ProjectScan> ScanAsync(
    string path,
    IProgress<(double Percent, string Message)>? progress = null,
    ...)
{
    progress?.Report((5.0, "Found N files"));  // ?. = safe null call
    ...
    progress?.Report((100.0, "Scan complete"));
}

// Caller creates a Progress<T> and binds it to the UI:
var prog = new Progress<(double Percent, string Message)>(t => {
    ProgressBar.Value = t.Percent;
    StatusLabel.Text  = t.Message;
});
await scanner.ScanAsync(path, progress: prog);
```

`IProgress<T>` automatically marshals the callback back to the UI thread —
no need for Python's `asyncio.run_in_executor` or thread-safe queue tricks.

---

## Summary

The logic in both versions is **identical**. The differences are:

1. **Syntax** — C# uses `{}` blocks, types declared before names, `//` comments
2. **Type system** — C# types are explicit and checked at compile time (no runtime surprises)
3. **Async model** — `Task<T>` / `await` instead of `Coroutine` / `await`; `Task.WhenAll` instead of `asyncio.gather`
4. **UI** — XAML + code-behind instead of React + TSX (both are declarative layout + event handlers)
5. **Server** — ASP.NET Core attributes instead of FastAPI decorators (same concept)
6. **Packages** — NuGet `.csproj` instead of pip `requirements.txt`
