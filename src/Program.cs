using System.Windows;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.DependencyInjection;
using QRD.Core.Infrastructure.AI;
using QRD.Core.Infrastructure.Parsers;
using QRD.Core.Infrastructure.PDF;
using QRD.Core.Infrastructure.Storage;
using QRD.Core.Infrastructure.Syntax;
using QRD.Core.Services;
using QRD.UI.Views;
using QRD.Utils;

namespace QRD;

public class Program
{
    [STAThread]
    public static void Main(string[] args)
    {
        var settings = AppSettings.Load();
        var langMgr  = new LanguageExtensionManager(settings);

        var apiThread = new Thread(() => RunApiServer(settings, args))
        {
            IsBackground = true,
            Name         = "QRD-API"
        };
        apiThread.Start();
        Thread.Sleep(400);

        var app = new App();
        app.InitializeComponent();
        app.Run(new MainWindow(settings, langMgr));
    }

    private static void RunApiServer(AppSettings settings, string[] args)
    {
        var builder = WebApplication.CreateBuilder(args);

        builder.Services.AddSingleton(settings);
        builder.Services.AddSingleton<TempManager>();
        builder.Services.AddSingleton<LanguageExtensionManager>();
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
                o.JsonSerializerOptions.PropertyNamingPolicy =
                    System.Text.Json.JsonNamingPolicy.SnakeCaseLower);

        builder.Services.AddCors(o => o.AddDefaultPolicy(p =>
            p.AllowAnyOrigin().AllowAnyHeader().AllowAnyMethod()));

        builder.WebHost.UseUrls($"http://{settings.Host}:{settings.Port}");

        var app = builder.Build();
        app.Services.GetRequiredService<TempManager>().Initialize();
        app.UseCors();
        app.UseRouting();
        app.MapControllers();
        app.Run();
    }
}
