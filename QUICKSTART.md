# QRD Quick Start

## Users (just want to run it)

1. Install [.NET 8 Desktop Runtime](https://dotnet.microsoft.com/download/dotnet/8.0)
2. Extract the zip, double-click **QRD.exe**
3. Scanner tab → Browse → Scan
4. Export tab → choose mode → Start Export
5. Click the output PDF link when done

Optional AI: Settings → paste Anthropic API key → Save → restart app

---

## Developers (want to build / modify)

```powershell
# 1. Install .NET 8 SDK  https://dotnet.microsoft.com/download/dotnet/8.0
# 2. Clone / extract the project
cd QRD-CSharp

# 3. Restore packages
dotnet restore

# 4. Run
dotnet run

# 5. Build release
dotnet publish -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true -o publish\
```

### Where things live

| You want to… | Look in… |
|---|---|
| Change scan logic | `src/Core/Services/ProjectScannerService.cs` |
| Change PDF layout | `src/Core/Infrastructure/PDF/PdfBuilder.cs` |
| Add an API route | `src/Api/Controllers/` |
| Change the UI | `src/UI/Views/*.xaml` |
| Change app config | `appsettings.json` |
| Add a new file type | `ExtToLanguage` / `ExtToCategory` in `ProjectScannerService.cs` |
