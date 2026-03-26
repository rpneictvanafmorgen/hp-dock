namespace HpDockFirmware.App.Models;

public sealed class InstallationResult
{
    public bool Success { get; init; }
    public int ExitCode { get; init; }
    public string Summary { get; init; } = string.Empty;
    public string LogFilePath { get; init; } = string.Empty;
    public string CommandLine { get; init; } = string.Empty;
}
