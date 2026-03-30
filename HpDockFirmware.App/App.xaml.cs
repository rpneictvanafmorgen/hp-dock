using System.IO;
using System.Windows;
using HpDockFirmware.App.Services;
using HpDockFirmware.App.ViewModels;

namespace HpDockFirmware.App;

public partial class App : Application
{
    private readonly string _startupLogPath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "HpDockFirmware",
        "Logs",
        "startup.log");

    public App()
    {
        AppDomain.CurrentDomain.UnhandledException += (_, args) =>
        {
            WriteStartupLog($"AppDomain exception: {args.ExceptionObject}");
        };

        DispatcherUnhandledException += (_, args) =>
        {
            WriteStartupLog($"Dispatcher exception: {args.Exception}");
            MessageBox.Show(args.Exception.Message, "Startup error", MessageBoxButton.OK, MessageBoxImage.Error);
            args.Handled = true;
        };
    }

    protected override async void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        try
        {
            WriteStartupLog($"Startup at {DateTime.Now:O}. BaseDirectory={AppContext.BaseDirectory}");

            var viewModel = new MainViewModel(
                new DockDetectionService(),
                new FirmwareCatalogService(AppContext.BaseDirectory),
                new HpCatalogRefreshService(AppContext.BaseDirectory),
                new FirmwareDownloadService(),
                new SoftPaqExtractionService(),
                new ProcessRunnerService(),
                new LogService());

            var window = new MainWindow
            {
                DataContext = viewModel
            };

            MainWindow = window;
            window.Show();
            window.Activate();
            await viewModel.InitializeAsync();
        }
        catch (Exception ex)
        {
            WriteStartupLog($"Startup failed: {ex}");
            MessageBox.Show(ex.Message, "Startup error", MessageBoxButton.OK, MessageBoxImage.Error);
            Shutdown(-1);
        }
    }

    private void WriteStartupLog(string message)
    {
        var directory = Path.GetDirectoryName(_startupLogPath)!;
        Directory.CreateDirectory(directory);
        File.AppendAllText(_startupLogPath, $"{message}{Environment.NewLine}");
    }
}
