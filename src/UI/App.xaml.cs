using System.Windows;

namespace QRD;

public partial class App : Application
{
    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        // Global exception handler
        DispatcherUnhandledException += (_, ex) =>
        {
            MessageBox.Show($"Unexpected error: {ex.Exception.Message}",
                "QRD Error", MessageBoxButton.OK, MessageBoxImage.Error);
            ex.Handled = true;
        };
    }
}
