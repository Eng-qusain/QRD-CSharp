# QRD — Quantum Repo Documenter (C# Edition)

> Convert any software project into professional PDF documentation — automatically.
> 
> This is a complete rebuild of the original Python + Electron app in **C# / .NET 8**.  
> No Python runtime. No Node.js. No Electron. One native Windows executable.

---

## Table of Contents

1. [What the app does](#what-the-app-does)
2. [User Guide — How to use it](#user-guide)
3. [Developer Guide — How it works (for someone who knows Python)](#developer-guide)
4. [Project structure explained](#project-structure)
5. [How to build and run](#how-to-build-and-run)
6. [Configuration reference](#configuration-reference)
7. [Python → C# translation guide](#python-to-csharp-translation)

---

## What the app does

QRD scans a code repository folder on your computer and produces beautiful PDF documentation:

- **File tree** with language detection and line counts
- **Syntax-highlighted source code** (4 colour themes)
- **Clickable table of contents**
- **Project statistics** — files, lines of code, language breakdown
- **CSV / Excel previews** — schema + sample rows
- **Image embedding** — PNG, JPG, WEBP
- **AI summaries** (optional) — per-file purpose, key functions, complexity rating
- **Petroleum well data** — LAS/DLIS curve lists (industry feature)

---

## User Guide

### Requirements

| What | Version |
|------|---------|
| Windows | 10 or 11 (64-bit) |
| .NET 8 Runtime | Free download from microsoft.com/dotnet |
| API key | **Optional** — only needed for AI summaries |

---

### Installation

1. Download `QRD-v1.0.0-win-x64.zip`
2. Extract it anywhere (e.g. `C:\Tools\QRD\`)
3. Double-click **QRD.exe** — that's it

> **First run tip:** Windows may show a SmartScreen warning because the app
> isn't signed. Click "More info" → "Run anyway". This is normal for apps
> without a paid code-signing certificate.

---

### Using the app

#### Step 1 — Scanner tab

1. Click **Scanner** in the left sidebar
2. Click **Browse…** and pick your project folder
3. Click **Scan** — a progress bar shows the scan running
4. The file tree appears on the left; click any file to see its details

#### Step 2 — Export tab

1. Click **Export** in the left sidebar
2. The project path carries over automatically; set an **Output Folder**
3. Choose an **Export Mode**:

| Mode | What you get | Best for |
|------|-------------|----------|
| A — Single PDF | One big PDF for the whole project | Client handoffs, code reviews |
| B — Folder PDFs | One PDF per top-level folder | Large modular projects |
| C — Per-File PDFs | One PDF per source file | Audit trails |
| D — Full Package | Combined + folder PDFs | Portfolio, due diligence |

4. Tick/untick options (AI Summaries, TOC, Stats, Line Numbers, etc.)
5. Choose a **Theme** (Default = GitHub Light, Dark, GitHub Neutral, Monokai)
6. Click **Start Export** — a progress bar tracks each file being processed
7. When done, clickable file links appear — click one to open the PDF immediately

#### Step 3 — Settings tab

- **Anthropic API Key** — paste your key from `console.anthropic.com` to enable AI summaries
- **OpenAI API Key** — fallback if you prefer GPT-4o-mini
- **Default Output Folder** — where PDFs are saved
- **Workers** — how many files to document with AI in parallel (3 is usually fine)

---

### AI summaries — optional

The app works fine without any API key. If you add one:

1. Go to **Settings**
2. Paste your Anthropic API key in the box
3. Click **Save AI Settings**
4. Restart QRD
5. On the next export, tick **AI Summaries** — each source file gets a summary card in the PDF

AI costs roughly $0.01–$0.10 per project depending on size (uses Claude Haiku by default, the cheapest model).

---

## Developer Guide

This section is written for someone who **knows Python** but is new to C#.

---

### The Python original vs this C# version

| Python original | C# equivalent | Notes |
|----------------|---------------|-------|
| FastAPI server | ASP.NET Core (built-in) | Same concept — HTTP routes handle requests |
| `main.py` startup | `Program.cs` | Entry point that configures and starts everything |
| `pydantic` models | C# `record` / `class` | Type-safe data containers |
| `pydantic-settings` | `AppSettings.cs` | Reads from `appsettings.json` + env vars |
| `dataclass` | C# `class` with properties | Same idea, different syntax |
| Python `async def` | C# `async Task<T>` | Both are async/await — very similar |
| `asyncio.gather()` | `Task.WhenAll()` | Run multiple async tasks in parallel |
| `asyncio.to_thread()` | `Task.Run()` | Run blocking code on a background thread |
| Electron (UI) | WPF (UI) | Both are desktop UI frameworks; WPF is native Windows |
| React/TypeScript (renderer) | XAML + C# code-behind | XAML is like HTML for WPF; code-behind is the logic |
| `requirements.txt` | `.csproj` `<PackageReference>` | Same idea — lists dependencies |
| `pip install` | `dotnet restore` | Downloads packages |
| `uvicorn main:app` | Embedded in `Program.cs` | No separate server process needed |
| ReportLab (PDF) | MigraDoc / PdfSharp | Different library, same concept |
| `anthropic` Python SDK | `Anthropic.SDK` NuGet | Official C# SDK, same API |

---

### How the app starts (Program.cs)

```
Program.Main()
│
├── Starts embedded ASP.NET Core server on a background thread
│   └── Registers all services (scanner, AI, PDF builder, etc.)
│   └── Maps HTTP routes (/scanner/scan, /export/start, etc.)
│
└── Starts WPF app on the main thread
    └── Shows MainWindow
    └── Each page talks to the local API via HttpClient
```

In Python this was two separate processes (uvicorn + Electron). Here it's one `.exe`.

---

### How a scan works (ProjectScannerService.cs)

```
ScanAsync(path)
│
├── WalkDirectory()          ← like Python's os.walk() but synchronous, runs in Task.Run()
├── for each 500-file chunk:
│   └── Task.WhenAll(ProcessFile x 500)  ← like asyncio.gather()
│       └── reads bytes, detects binary, counts lines
├── BuildTree()              ← groups files into DirectoryNode hierarchy
└── ComputeStats()           ← counts languages, sizes, etc.
```

The equivalent Python code was in `scanner_service.py`. The logic is identical; only the syntax differs.

---

### How an export works (ExportOrchestratorService.cs)

```
StartExport()
│
├── Creates ExportJob with a unique ID
├── Fires off RunExportAsync() on a background task (fire-and-forget)
└── Returns the job ID immediately (non-blocking)

RunExportAsync()
│
├── Step 1: ScanAsync() — scan the project
├── Step 2: GenerateAiSummariesAsync() — if AI enabled, document each source file
│           └── SemaphoreSlim(3) — only 3 concurrent AI calls (rate limit safety)
├── Step 3: PdfBuilder.BuildSinglePdfAsync() / BuildFolderPdfsAsync()
└── Sets job.Status = "completed" (or "failed")

The UI polls /export/{jobId}/status every 500ms to update the progress bar.
```

---

### Key C# concepts explained for Python devs

#### 1. `async Task<T>` vs Python `async def`

```python
# Python
async def scan(path: str) -> ProjectScan:
    result = await do_something()
    return result
```

```csharp
// C# — almost identical!
async Task<ProjectScan> ScanAsync(string path)
{
    var result = await DoSomethingAsync();
    return result;
}
```

The main difference: C# requires you to declare the return type (`Task<ProjectScan>`).
`Task<T>` is C#'s equivalent of Python's `Coroutine[T]`.

#### 2. Properties vs Python dataclass fields

```python
# Python
@dataclass
class FileInfo:
    name: str
    size_bytes: int
    
    @property
    def size_kb(self) -> float:
        return self.size_bytes / 1024
```

```csharp
// C#
public class FileInfo
{
    public string Name { get; init; } = "";
    public long SizeBytes { get; init; }
    
    // Computed property — same as Python @property
    public double SizeKb => SizeBytes / 1024.0;
}
```

`{ get; init; }` means: readable anywhere, but only settable during object creation.
This is like making a Python dataclass field `frozen=True`.

#### 3. Dependency injection vs Python manually passing objects

In Python you'd pass the scanner to the orchestrator manually:
```python
orchestrator = ExportOrchestratorService(
    scanner=ProjectScannerService(),
    ai_documenter=AIDocumenter(settings),
    ...
)
```

In C# / ASP.NET Core, the framework does this automatically:
```csharp
// In Program.cs — register services
builder.Services.AddSingleton<ProjectScannerService>();
builder.Services.AddSingleton<AIDocumenter>();

// In ExportOrchestratorService — ask for them via constructor
public class ExportOrchestratorService(
    ProjectScannerService scanner,     // injected automatically
    AIDocumenter aiDocumenter,         // injected automatically
    PdfBuilder pdfBuilder)             // injected automatically
```

The framework sees that `ExportOrchestratorService` needs a `ProjectScannerService`
and automatically provides the one you registered. This is called **Dependency Injection**.

#### 4. XAML (the UI language)

XAML is to WPF what HTML is to a browser. Each `.xaml` file defines the layout;
the matching `.xaml.cs` (called the "code-behind") contains the event handlers.

```xml
<!-- ScannerPage.xaml — layout -->
<Button Content="Scan" Click="Scan_Click"/>
```

```csharp
// ScannerPage.xaml.cs — logic
private async void Scan_Click(object sender, RoutedEventArgs e)
{
    // runs when the button is clicked
}
```

This is the same pattern as Electron's `.tsx` file (JSX) + its event handlers.

#### 5. NuGet packages = pip packages

| Python | C# |
|--------|-----|
| `pip install anthropic` | Add `<PackageReference Include="Anthropic.SDK" Version="3.1.0"/>` to `.csproj` |
| `pip install fastapi` | `<PackageReference Include="Microsoft.AspNetCore.App"/>` |
| `pip install reportlab` | `<PackageReference Include="PdfSharp-WPF"/>` |
| `pip install csvhelper` | `<PackageReference Include="CsvHelper"/>` |
| `pip install openpyxl` | `<PackageReference Include="ExcelDataReader"/>` |

Run `dotnet restore` to download them (like `pip install -r requirements.txt`).

---

## Project Structure

```
QRD-CSharp/
│
├── QRD.csproj                    ← Project file (like requirements.txt + build config)
├── appsettings.json              ← Config (like .env)
│
└── src/
    ├── Program.cs                ← Entry point — starts API + WPF
    │
    ├── Utils/
    │   └── AppSettings.cs        ← Like Python's pydantic Settings class
    │
    ├── Core/
    │   ├── Domain/
    │   │   └── Entities/
    │   │       └── Entities.cs   ← Like Python's entities.py dataclasses
    │   │
    │   ├── Services/
    │   │   ├── ProjectScannerService.cs   ← scanner_service.py
    │   │   └── ExportOrchestratorService.cs ← export_orchestrator.py
    │   │
    │   └── Infrastructure/
    │       ├── AI/
    │       │   └── AIDocumenter.cs        ← ai_documenter.py
    │       ├── PDF/
    │       │   └── PdfBuilder.cs          ← pdf_builder.py (uses MigraDoc)
    │       ├── Parsers/
    │       │   └── Parsers.cs             ← code_parser.py / csv_parser.py / etc.
    │       └── Storage/
    │           └── TempManager.cs         ← temp_manager.py
    │
    ├── Api/
    │   └── Controllers/
    │       ├── ScannerController.cs       ← api/routes/scanner.py
    │       ├── ExportController.cs        ← api/routes/export.py
    │       └── HealthController.cs        ← api/routes/health.py + ai.py
    │
    └── UI/
        ├── App.xaml / App.xaml.cs         ← Electron's App.tsx equivalent
        └── Views/
            ├── MainWindow.xaml/.cs        ← MainLayout.tsx (sidebar + navigation)
            ├── DashboardPage.xaml/.cs     ← DashboardPage.tsx
            ├── ScannerPage.xaml/.cs       ← ScannerPage.tsx
            ├── ExportPage.xaml/.cs        ← ExportPage.tsx
            └── SettingsPage.xaml/.cs      ← SettingsPage.tsx
```

---

## How to Build and Run

### Prerequisites

```
# Install .NET 8 SDK (free):
https://dotnet.microsoft.com/download/dotnet/8.0
# Choose: Windows x64 SDK installer
```

Verify installation:
```powershell
dotnet --version
# Should show: 8.x.x
```

### Restore packages and run in development

```powershell
cd QRD-CSharp

# Download all NuGet packages (like pip install -r requirements.txt)
dotnet restore

# Run in development mode (auto-reloads on code changes with dotnet-watch)
dotnet run

# Or build first, then run the .exe directly:
dotnet build
.\bin\Debug\net8.0-windows\QRD.exe
```

### Build a release executable

```powershell
# Single self-contained .exe (no .NET runtime needed on target machine)
dotnet publish -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true -o publish\

# The output is in publish\QRD.exe — copy that folder to any Windows 10/11 PC
```

### Run with AI enabled

```powershell
# Option 1: edit appsettings.json
# Set "AnthropicApiKey": "sk-ant-..."

# Option 2: environment variable (no file change needed)
$env:ANTHROPIC_API_KEY = "sk-ant-..."
.\QRD.exe
```

---

## Configuration Reference

`appsettings.json` (in the same folder as QRD.exe):

```jsonc
{
  "Host": "127.0.0.1",        // API server bind address (don't change)
  "Port": 8765,               // API server port
  "Env": "production",        // "development" enables /docs Swagger UI

  "AnthropicApiKey": "",      // Get from console.anthropic.com
  "OpenAiApiKey": "",         // Fallback AI provider
  "AiModel": "claude-3-5-haiku-20241022",  // Change to claude-opus-4-6 for better quality
  "AiEnabled": true,

  "OutputDir": "",            // Blank = ~/Documents/QRD
  "TempDir": "",              // Blank = ~/.qrd/temp
  "MaxFileSizeMb": 50,        // Skip files larger than this
  "MaxConcurrentWorkers": 3,  // Parallel AI calls
  "ScanChunkSize": 500        // Files processed per batch
}
```

All settings can also be set as environment variables prefixed with `QRD_`:
```powershell
$env:QRD_PORT = "9000"
$env:QRD_MAXFILESIZEMB = "100"
```

---

## Python → C# Translation

Quick reference for things that look different:

| Python | C# | Notes |
|--------|-----|-------|
| `def foo(x: int) -> str:` | `string Foo(int x)` | Return type comes first in C# |
| `print("hello")` | `Console.WriteLine("hello")` | |
| `f"Hello {name}"` | `$"Hello {name}"` | `$` instead of `f` |
| `None` | `null` | Same concept |
| `Optional[str]` | `string?` | `?` suffix = nullable |
| `list[str]` | `List<string>` | |
| `dict[str, int]` | `Dictionary<string, int>` | |
| `isinstance(x, str)` | `x is string` | |
| `x if cond else y` | `cond ? x : y` | Ternary operator |
| `for item in items:` | `foreach (var item in items)` | |
| `[x for x in lst if x > 0]` | `lst.Where(x => x > 0).ToList()` | LINQ |
| `map(fn, lst)` | `lst.Select(fn).ToList()` | LINQ |
| `sum(x.size for x in files)` | `files.Sum(x => x.Size)` | LINQ |
| `sorted(items, key=lambda x: x.name)` | `items.OrderBy(x => x.Name)` | LINQ |
| `@dataclass` | `class` with auto-properties | |
| `@property` | `public T Prop => expression;` | |
| `raise ValueError("msg")` | `throw new ArgumentException("msg")` | |
| `try/except Exception as e:` | `try { } catch (Exception ex) { }` | |
| `with open(path) as f:` | `using var f = File.OpenRead(path);` | |
| `Path.join(a, b)` | `Path.Combine(a, b)` | |
| `os.path.exists(p)` | `File.Exists(p)` or `Directory.Exists(p)` | |
| `__init__(self, x)` | Constructor: `public MyClass(int x)` | |
| `self.x` | `this.x` (or just `x` inside the class) | |
| `logging.getLogger(__name__)` | `ILogger<MyClass>` (injected) | |

---

## Frequently Asked Questions

**Q: Do I need to install Python?**  
No. Zero Python. This is a pure .NET 8 application.

**Q: Does it work on macOS or Linux?**  
WPF is Windows-only. The API backend (ASP.NET Core) and all the scanning/export
logic can run cross-platform, but the WPF GUI only works on Windows.
A future version could use Avalonia UI for cross-platform support.

**Q: The app won't start / crashes immediately**  
Make sure .NET 8 Desktop Runtime is installed:
`winget install Microsoft.DotNet.DesktopRuntime.8`

**Q: AI summaries are not showing up**  
Check Settings — the API key must be set and the app restarted after saving.
Also check that "AI Summaries" is ticked in the Export options.

**Q: Port 8765 is already in use**  
Change `"Port": 8765` in `appsettings.json` to any free port (e.g. 9001).

**Q: How do I add support for a new file type?**  
In `ProjectScannerService.cs`, add the extension to `ExtToLanguage` and `ExtToCategory`.
In `PdfBuilder.cs`, add handling in `AddFileSectionAsync()` if the file needs special rendering.
