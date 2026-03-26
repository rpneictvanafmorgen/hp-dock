using System.Management;
using System.Text.RegularExpressions;
using HpDockFirmware.App.Models;

namespace HpDockFirmware.App.Services;

public sealed class DockDetectionService
{
    private static readonly Regex VidPidRegex = new(@"VID_([0-9A-F]{4}).*PID_([0-9A-F]{4})", RegexOptions.IgnoreCase | RegexOptions.Compiled);
    private static readonly string[] DockVendorIds =
    [
        "17E9", // DisplayLink
        "0BDA", // Realtek
        "2109", // VIA Labs
        "174C", // ASMedia
        "05E3", // Genesys Logic
        "04B4", // Cypress / Infineon
        "1D5C"  // Fresco Logic
    ];
    private static readonly string[] DockKeywords =
    [
        "dock",
        "thunderbolt dock",
        "usb-c dock",
        "universal dock",
        "travel hub",
        "usb-c/a",
        "essential",
        "displaylink",
        "billboard",
        "usb ethernet",
        "gigabit network",
        "usb hub",
        "thunderbolt"
    ];
    private static readonly string[] NonDockKeywords =
    [
        "zbook",
        "elitebook",
        "probook",
        "notebook",
        "laptop",
        "desktop",
        "workstation",
        "monitor"
    ];
    private static readonly string[] DockClassKeywords =
    [
        "usb",
        "net",
        "display",
        "system"
    ];
    public async Task<IReadOnlyList<DockDeviceInfo>> DetectAsync(CancellationToken cancellationToken = default)
    {
        var snapshot = await CaptureSnapshotAsync(cancellationToken);
        return snapshot.DetectedDocks;
    }

    public Task<DockDetectionSnapshot> CaptureSnapshotAsync(CancellationToken cancellationToken = default)
    {
        return Task.Run(() =>
        {
            var devices = new List<DockDeviceInfo>();
            var candidates = new List<DockDetectionCandidate>();
            var rawInventory = new List<DockDetectionCandidate>();
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var thunderboltSignals = new List<DockDetectionCandidate>();

            TryDetectHpWmi(devices, candidates, seen);
            TryDetectPnP(devices, candidates, rawInventory, thunderboltSignals, seen);

            var thunderboltHostPresent = thunderboltSignals.Any(signal =>
                signal.Reason?.Contains("thunderbolt host", StringComparison.OrdinalIgnoreCase) == true);
            var thunderboltPeripheralPresent = thunderboltSignals.Any(signal =>
                signal.Reason?.Contains("thunderbolt peripheral", StringComparison.OrdinalIgnoreCase) == true);
            var advisory = thunderboltHostPresent && !thunderboltPeripheralPresent
                ? "Thunderbolt host support is present, but no Thunderbolt peripheral or dock controller is enumerating. This commonly means dock authorization, cable, power, or Thunderbolt driver/firmware issues."
                : null;

            return new DockDetectionSnapshot
            {
                DetectedDocks = devices
                    .OrderByDescending(d => d.IsHpDevice)
                    .ThenBy(d => d.ModelName, StringComparer.OrdinalIgnoreCase)
                    .ToList(),
                Candidates = candidates
                    .OrderBy(c => c.Source, StringComparer.OrdinalIgnoreCase)
                    .ThenBy(c => c.Name, StringComparer.OrdinalIgnoreCase)
                    .ToList(),
                RawInventory = rawInventory
                    .OrderBy(c => c.Source, StringComparer.OrdinalIgnoreCase)
                    .ThenBy(c => c.Name, StringComparer.OrdinalIgnoreCase)
                    .ToList(),
                ThunderboltHostPresent = thunderboltHostPresent,
                ThunderboltPeripheralPresent = thunderboltPeripheralPresent,
                Advisory = advisory
            };
        }, cancellationToken);
    }

    private static void TryDetectHpWmi(List<DockDeviceInfo> devices, List<DockDetectionCandidate> candidates, HashSet<string> seen)
    {
        try
        {
            var scope = new ManagementScope(@"\\.\root\HP\InstrumentedServices\v1");
            scope.Connect();

            if (!scope.IsConnected)
            {
                return;
            }

            var query = new ObjectQuery("SELECT * FROM HP_DockAccessory");
            using var searcher = new ManagementObjectSearcher(scope, query);
            using var results = searcher.Get();

            foreach (ManagementObject result in results)
            {
                var modelName = ReadProperty(result, "Name") ?? ReadProperty(result, "Model") ?? "HP Dock";
                var serial = ReadProperty(result, "SerialNumber");
                var firmware = ReadProperty(result, "FirmwareVersion");
                var instanceId = ReadProperty(result, "PNPDeviceID");
                var key = instanceId ?? $"{modelName}|{serial}";
                candidates.Add(new DockDetectionCandidate
                {
                    Source = "HP WMI",
                    Name = modelName,
                    Manufacturer = "HP",
                    DeviceInstanceId = instanceId,
                    Reason = "Reported by HP_DockAccessory."
                });

                if (!seen.Add(key))
                {
                    continue;
                }

                devices.Add(new DockDeviceInfo
                {
                    Source = "HP WMI",
                    ModelName = modelName,
                    FriendlyName = modelName,
                    SerialNumber = serial,
                    FirmwareVersion = firmware,
                    DeviceInstanceId = instanceId,
                    IsHpDevice = true
                });
            }
        }
        catch
        {
            // HP WMI is optional. Falling back to generic PnP detection is expected.
            candidates.Add(new DockDetectionCandidate
            {
                Source = "HP WMI",
                Name = "HP_DockAccessory",
                Reason = "HP WMI query unavailable on this system."
            });
        }
    }

    private static void TryDetectPnP(List<DockDeviceInfo> devices, List<DockDetectionCandidate> candidates, List<DockDetectionCandidate> rawInventory, List<DockDetectionCandidate> thunderboltSignals, HashSet<string> seen)
    {
        const string queryText = """
            SELECT Name, DeviceID, PNPDeviceID, HardwareID, Manufacturer, PNPClass, Service
            FROM Win32_PnPEntity
            WHERE PNPDeviceID IS NOT NULL
            """;

        using var searcher = new ManagementObjectSearcher(queryText);
        using var results = searcher.Get();

        foreach (ManagementObject result in results)
        {
            var name = ReadProperty(result, "Name");
            var deviceId = ReadProperty(result, "PNPDeviceID") ?? ReadProperty(result, "DeviceID");
            var manufacturer = ReadProperty(result, "Manufacturer");
            var deviceClass = ReadProperty(result, "PNPClass");
            var service = ReadProperty(result, "Service");
            var hardwareIds = ReadArrayProperty(result, "HardwareID");
            var hardwareId = hardwareIds.FirstOrDefault(id => id.Contains("VID_", StringComparison.OrdinalIgnoreCase))
                ?? hardwareIds.FirstOrDefault();

            if (string.IsNullOrWhiteSpace(name) || string.IsNullOrWhiteSpace(deviceId))
            {
                continue;
            }

            var isHp = name.Contains("HP", StringComparison.OrdinalIgnoreCase)
                || string.Equals(manufacturer, "HP", StringComparison.OrdinalIgnoreCase)
                || string.Equals(manufacturer, "HP Inc.", StringComparison.OrdinalIgnoreCase)
                || (hardwareId?.Contains("VID_03F0", StringComparison.OrdinalIgnoreCase) ?? false);
            var vendorMatch = hardwareId is null ? null : VidPidRegex.Match(hardwareId);
            var vendorId = vendorMatch?.Success == true ? vendorMatch.Groups[1].Value.ToUpperInvariant() : null;
            var isKnownDockVendor = vendorId is not null && DockVendorIds.Contains(vendorId, StringComparer.OrdinalIgnoreCase);
            var productId = vendorMatch?.Success == true ? vendorMatch.Groups[2].Value.ToUpperInvariant() : null;
            var isThunderboltHostSignal =
                string.Equals(vendorId, "8086", StringComparison.OrdinalIgnoreCase)
                && (
                    name.Contains("Thunderbolt", StringComparison.OrdinalIgnoreCase)
                    || deviceId.Contains("DEV_15EA", StringComparison.OrdinalIgnoreCase)
                    || deviceId.Contains("DEV_15EF", StringComparison.OrdinalIgnoreCase)
                    || deviceId.Contains("DEV_15D2", StringComparison.OrdinalIgnoreCase)
                    || deviceId.Contains("TBFP", StringComparison.OrdinalIgnoreCase));
            var isThunderboltPeripheralSignal =
                name.Contains("Thunderbolt", StringComparison.OrdinalIgnoreCase)
                && !isThunderboltHostSignal;

            var looksLikeDock = DockKeywords.Any(keyword => name.Contains(keyword, StringComparison.OrdinalIgnoreCase));
            var looksLikeNonDock = NonDockKeywords.Any(keyword => name.Contains(keyword, StringComparison.OrdinalIgnoreCase));
            var isDockClass = deviceClass is not null && DockClassKeywords.Any(keyword => deviceClass.Contains(keyword, StringComparison.OrdinalIgnoreCase));
            var isLikelyDockHardware = isKnownDockVendor
                && (name.Contains("hub", StringComparison.OrdinalIgnoreCase)
                    || name.Contains("thunderbolt", StringComparison.OrdinalIgnoreCase)
                    || name.Contains("usb-c", StringComparison.OrdinalIgnoreCase)
                    || name.Contains("ethernet", StringComparison.OrdinalIgnoreCase)
                    || name.Contains("billboard", StringComparison.OrdinalIgnoreCase)
                    || name.Contains("displaylink", StringComparison.OrdinalIgnoreCase));
            var shouldIncludeInRawInventory =
                isDockClass
                || looksLikeDock
                || isKnownDockVendor
                || name.Contains("ethernet", StringComparison.OrdinalIgnoreCase)
                || name.Contains("hub", StringComparison.OrdinalIgnoreCase)
                || name.Contains("billboard", StringComparison.OrdinalIgnoreCase)
                || name.Contains("displaylink", StringComparison.OrdinalIgnoreCase)
                || name.Contains("thunderbolt", StringComparison.OrdinalIgnoreCase)
                || name.Contains("usb", StringComparison.OrdinalIgnoreCase);

            if (isThunderboltHostSignal || isThunderboltPeripheralSignal)
            {
                thunderboltSignals.Add(new DockDetectionCandidate
                {
                    Source = "Thunderbolt",
                    Name = name,
                    Manufacturer = manufacturer,
                    DeviceClass = deviceClass,
                    DeviceInstanceId = deviceId,
                    HardwareId = hardwareId,
                    Reason = isThunderboltPeripheralSignal ? "thunderbolt peripheral" : "thunderbolt host"
                });
            }

            if (shouldIncludeInRawInventory)
            {
                rawInventory.Add(new DockDetectionCandidate
                {
                    Source = "Raw PnP",
                    Name = name,
                    Manufacturer = manufacturer,
                    DeviceClass = deviceClass,
                    DeviceInstanceId = deviceId,
                    HardwareId = hardwareId,
                    Reason = BuildRawInventoryReason(vendorId, productId, service, isKnownDockVendor)
                });
            }

            if (looksLikeDock || isLikelyDockHardware || isHp || (isKnownDockVendor && isDockClass))
            {
                candidates.Add(new DockDetectionCandidate
                {
                    Source = "PnP",
                    Name = name,
                    Manufacturer = manufacturer,
                    DeviceClass = deviceClass,
                    DeviceInstanceId = deviceId,
                    HardwareId = hardwareId,
                    Reason = BuildCandidateReason(isHp, isKnownDockVendor, looksLikeDock, isLikelyDockHardware, looksLikeNonDock, deviceClass)
                });
            }

            if ((!isHp && !isKnownDockVendor) || looksLikeNonDock || (!looksLikeDock && !isLikelyDockHardware))
            {
                continue;
            }

            if (!seen.Add(deviceId))
            {
                continue;
            }

            devices.Add(new DockDeviceInfo
            {
                Source = "PnP",
                ModelName = name,
                FriendlyName = name,
                DeviceInstanceId = deviceId,
                HardwareId = hardwareId,
                VendorId = vendorMatch?.Success == true ? vendorMatch.Groups[1].Value.ToUpperInvariant() : null,
                ProductId = vendorMatch?.Success == true ? vendorMatch.Groups[2].Value.ToUpperInvariant() : null,
                IsHpDevice = true
            });
        }
    }

    private static string BuildCandidateReason(bool isHp, bool isKnownDockVendor, bool looksLikeDock, bool isLikelyDockHardware, bool looksLikeNonDock, string? deviceClass)
    {
        var reasons = new List<string>();
        if (isHp)
        {
            reasons.Add("HP-related device");
        }

        if (isKnownDockVendor)
        {
            reasons.Add("known dock chipset vendor");
        }

        if (looksLikeDock)
        {
            reasons.Add("name looks dock-like");
        }

        if (isLikelyDockHardware)
        {
            reasons.Add("USB hardware looks dock-like");
        }

        if (looksLikeNonDock)
        {
            reasons.Add("excluded as non-dock");
        }

        if (!string.IsNullOrWhiteSpace(deviceClass))
        {
            reasons.Add($"class {deviceClass}");
        }

        return string.Join(", ", reasons);
    }

    private static string BuildRawInventoryReason(string? vendorId, string? productId, string? service, bool isKnownDockVendor)
    {
        var reasons = new List<string>();

        if (!string.IsNullOrWhiteSpace(vendorId))
        {
            reasons.Add($"VID {vendorId}");
        }

        if (!string.IsNullOrWhiteSpace(productId))
        {
            reasons.Add($"PID {productId}");
        }

        if (!string.IsNullOrWhiteSpace(service))
        {
            reasons.Add($"service {service}");
        }

        if (isKnownDockVendor)
        {
            reasons.Add("known dock vendor");
        }

        return string.Join(", ", reasons);
    }

    private static string? ReadProperty(ManagementObject managementObject, string propertyName)
    {
        try
        {
            return managementObject[propertyName]?.ToString();
        }
        catch
        {
            return null;
        }
    }

    private static IReadOnlyList<string> ReadArrayProperty(ManagementObject managementObject, string propertyName)
    {
        try
        {
            return (managementObject[propertyName] as string[]) ?? [];
        }
        catch
        {
            return [];
        }
    }
}
