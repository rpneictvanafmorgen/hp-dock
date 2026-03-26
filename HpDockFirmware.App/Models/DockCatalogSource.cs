namespace HpDockFirmware.App.Models;

public sealed class DockCatalogSource
{
    public string Id { get; init; } = string.Empty;
    public string DockModel { get; init; } = string.Empty;
    public string? ProductId { get; init; }
    public string? DetectPattern { get; init; }
    public string SourceUrl { get; init; } = string.Empty;
    public string PackageDisplayName { get; init; } = string.Empty;
    public string InstallerFileName { get; init; } = "HPFirmwareInstaller.exe";
    public string? InstallerArguments { get; init; }
    public string? SourceHint { get; init; }
    public string? Notes { get; init; }
}
