namespace HpDockFirmware.App.Models;

public sealed class DockDetectionSnapshot
{
    public IReadOnlyList<DockDeviceInfo> DetectedDocks { get; init; } = [];
    public IReadOnlyList<DockDetectionCandidate> Candidates { get; init; } = [];
    public IReadOnlyList<DockDetectionCandidate> RawInventory { get; init; } = [];
    public bool ThunderboltHostPresent { get; init; }
    public bool ThunderboltPeripheralPresent { get; init; }
    public string? Advisory { get; init; }
}
