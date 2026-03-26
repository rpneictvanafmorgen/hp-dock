namespace HpDockFirmware.App.Models;

public sealed class FirmwarePackageInfo
{
    public string Id { get; init; } = string.Empty;
    public string DockModel { get; init; } = string.Empty;
    public string? ProductId { get; init; }
    public string? DetectPattern { get; init; }
    public string Version { get; init; } = string.Empty;
    public string PackageDisplayName { get; init; } = string.Empty;
    public string InstallerFileName { get; init; } = "HPFirmwareInstaller.exe";
    public string? InstallerArguments { get; init; }
    public string? DownloadUrl { get; init; }
    public string? Notes { get; init; }
    public string? SourceUrl { get; init; }
    public DateTimeOffset? LastCheckedUtc { get; init; }
    public string? SourceStatus { get; init; }
}
