using System.Diagnostics;
using System.IO;
using System.Text;
using HpDockFirmware.App.Models;

namespace HpDockFirmware.App.Services;

public sealed class ProcessRunnerService
{
    public async Task<InstallationResult> RunInstallerAsync(
        string installerPath,
        string? installerArguments,
        string logFilePath,
        CancellationToken cancellationToken = default)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(logFilePath)!);

        var startInfo = new ProcessStartInfo
        {
            FileName = installerPath,
            Arguments = installerArguments ?? string.Empty,
            CreateNoWindow = true,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            WorkingDirectory = Path.GetDirectoryName(installerPath) ?? Environment.CurrentDirectory
        };

        using var process = new Process { StartInfo = startInfo, EnableRaisingEvents = true };
        var output = new StringBuilder();

        process.OutputDataReceived += (_, args) =>
        {
            if (args.Data is not null)
            {
                output.AppendLine(args.Data);
            }
        };

        process.ErrorDataReceived += (_, args) =>
        {
            if (args.Data is not null)
            {
                output.AppendLine(args.Data);
            }
        };

        process.Start();
        process.BeginOutputReadLine();
        process.BeginErrorReadLine();

        await process.WaitForExitAsync(cancellationToken);

        await File.WriteAllTextAsync(logFilePath, output.ToString(), cancellationToken);

        var summary = process.ExitCode switch
        {
            0 => "Installer finished successfully.",
            1602 => "Installer was canceled by the user.",
            3010 => "Installer finished and requested a reboot.",
            _ => $"Installer exited with code {process.ExitCode}."
        };

        return new InstallationResult
        {
            Success = process.ExitCode is 0 or 3010,
            ExitCode = process.ExitCode,
            Summary = summary,
            LogFilePath = logFilePath,
            CommandLine = $"\"{installerPath}\" {installerArguments}".Trim()
        };
    }
}
