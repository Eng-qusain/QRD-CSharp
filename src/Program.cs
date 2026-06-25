using System.Windows;
using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.DependencyInjection;
using QRD.Core.Infrastructure.AI;
using QRD.Core.Infrastructure.Parsers;
using QRD.Core.Infrastructure.PDF;
using QRD.Core.Infrastructure.Storage;
using QRD.Core.Services;
using QRD.UI.Views;
using QRD.Utils;

namespace QRD;

/// <summary>
/// Application entry point.
/// Starts an embedded ASP.NET Core API server on a background thread,
/// then launches the WPF window on the main (STA) thread.
/// No Python, no Node.js, no separate server process needed.
/// </summary>
public class Program
{
    [STAThread]
    public static void Main(string[] args)
    {
        var settings = AppSettings.Load();

        // ── Start embedded HTTP API server on a background thread ──────────
        var apiThread = new Thread(() => RunApiServer(settings, args))
        {
            IsBackground = true,
            Name         = "QRD-API"
        };
        apiThread.Start();

        // Give ASP.NET Core a moment to bind before the UI tries to call it
        Thread.Sleep(400);

        // ── Start WPF UI on the main (STA) thread ─────────────────────────
        var app = new App();
        app.InitializeComponent();
        app.Run(new MainWindow(settings));
    }

    // ── Embedded ASP.NET Core server ──────────────────────────────────────────

    private static void RunApiServer(AppSettings settings, string[] args)
    {
        var builder = WebApplication.CreateBuilder(args);

        // Services (Dependency Injection — equivalent to FastAPI's DI)
        builder.Services.AddSingleton(settings);
        builder.Services.AddSingleton<TempManager>();
        builder.Services.AddSingleton<ProjectScannerService>();
        builder.Services.AddSingleton<CodeParser>();
        builder.Services.AddSingleton<CsvParser>();
        builder.Services.AddSingleton<ExcelParser>();
        builder.Services.AddSingleton<ImageParser>();
        builder.Services.AddSingleton<AIDocumenter>();
        builder.Services.AddSingleton<PdfBuilder>();
        builder.Services.AddSingleton<ExportOrchestratorService>();

        builder.Services.AddControllers()
            .AddJsonOptions(o =>
            {
                // snake_case JSON output to match the original Python API contract
                o.JsonSerializerOptions.PropertyNamingPolicy =
                    System.Text.Json.JsonNamingPolicy.SnakeCaseLower;
            });

        builder.Services.AddCors(o => o.AddDefaultPolicy(p =>
            p.AllowAnyOrigin().AllowAnyHeader().AllowAnyMethod()));

        builder.WebHost.UseUrls($"http://{settings.Host}:{settings.Port}");

        var app = builder.Build();

        // Initialise storage directories
        app.Services.GetRequiredService<TempManager>().Initialize();

        app.UseCors();
        app.UseRouting();
        app.MapControllers();

        app.Run();
    }
}
