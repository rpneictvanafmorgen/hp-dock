using System.IO;

namespace HpDockFirmware.App.Services;

public sealed class LogService
{
    private readonly string _logDirectory;

    public LogService()
    {
        _logDirectory = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "HpDockFirmware",
            "Logs");
    }

    public string CreateLogFilePath()
    {
        Directory.CreateDirectory(_logDirectory);
        return Path.Combine(_logDirectory, $"install-{DateTime.Now:yyyyMMdd-HHmmss}.log");
    }

    public string CreateDiagnosticsFilePath()
    {
        Directory.CreateDirectory(_logDirectory);
        return Path.Combine(_logDirectory, $"diagnostics-{DateTime.Now:yyyyMMdd-HHmmss}.txt");
    }
}
