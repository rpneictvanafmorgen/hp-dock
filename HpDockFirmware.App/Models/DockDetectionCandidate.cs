namespace HpDockFirmware.App.Models;

public sealed class DockDetectionCandidate
{
    public string Source { get; init; } = string.Empty;
    public string Name { get; init; } = string.Empty;
    public string? Manufacturer { get; init; }
    public string? DeviceClass { get; init; }
    public string? DeviceInstanceId { get; init; }
    public string? HardwareId { get; init; }
    public string? Reason { get; init; }
}
