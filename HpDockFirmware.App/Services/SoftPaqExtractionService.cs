using System.Diagnostics;
using System.IO;

namespace HpDockFirmware.App.Services;

public sealed class SoftPaqExtractionService
{
    public async Task<string> PrepareInstallerAsync(string packagePath, CancellationToken cancellationToken = default)
    {
        if (!File.Exists(packagePath))
        {
            throw new FileNotFoundException("Package file not found.", packagePath);
        }

        if (Path.GetFileName(packagePath).Equals("HPFirmwareInstaller.exe", StringComparison.OrdinalIgnoreCase))
        {
            return packagePath;
        }

        if (!Path.GetExtension(packagePath).Equals(".exe", StringComparison.OrdinalIgnoreCase))
        {
            return packagePath;
        }

        var extractRoot = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "HpDockFirmware",
            "Extracted",
            Path.GetFileNameWithoutExtension(packagePath));

        Directory.CreateDirectory(extractRoot);

        if (!await TryExtractAsync(packagePath, $"-pdf -f\"{extractRoot}\" -s", cancellationToken)
            && !await TryExtractAsync(packagePath, $"/e /f \"{extractRoot}\" /s", cancellationToken))
        {
            return packagePath;
        }

        var installer = Directory
            .EnumerateFiles(extractRoot, "HPFirmwareInstaller.exe", SearchOption.AllDirectories)
            .FirstOrDefault();

        return installer ?? packagePath;
    }

    private static async Task<bool> TryExtractAsync(string packagePath, string arguments, CancellationToken cancellationToken)
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = packagePath,
            Arguments = arguments,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            WorkingDirectory = Path.GetDirectoryName(packagePath) ?? Environment.CurrentDirectory
        };

        using var process = Process.Start(startInfo);
        if (process is null)
        {
            return false;
        }

        await process.WaitForExitAsync(cancellationToken);
        return process.ExitCode == 0;
    }
}
