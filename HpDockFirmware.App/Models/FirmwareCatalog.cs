namespace HpDockFirmware.App.Models;

public sealed class FirmwareCatalog
{
    public string Version { get; init; } = "1";
    public DateTimeOffset? GeneratedAtUtc { get; init; }
    public string? GeneratedFrom { get; init; }
    public List<FirmwarePackageInfo> Packages { get; init; } = [];
}
