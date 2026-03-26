namespace HpDockFirmware.App.Models;

public sealed class DockDeviceInfo
{
    public string Source { get; init; } = "Unknown";
    public string ModelName { get; init; } = "Unknown HP Dock";
    public string? FriendlyName { get; init; }
    public string? SerialNumber { get; init; }
    public string? FirmwareVersion { get; init; }
    public string? DeviceInstanceId { get; init; }
    public string? HardwareId { get; init; }
    public string? VendorId { get; init; }
    public string? ProductId { get; init; }
    public bool IsHpDevice { get; init; }

    public string DisplayName => string.IsNullOrWhiteSpace(FriendlyName) ? ModelName : FriendlyName!;
}
