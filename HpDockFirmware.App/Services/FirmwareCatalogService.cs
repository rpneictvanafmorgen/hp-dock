using System.IO;
using System.Text.Json;
using HpDockFirmware.App.Models;

namespace HpDockFirmware.App.Services;

public sealed class FirmwareCatalogService
{
    private readonly string _bundledCatalogPath;
    private readonly string _localCatalogPath;
    private readonly JsonSerializerOptions _serializerOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        WriteIndented = true
    };

    public FirmwareCatalogService(string baseDirectory)
    {
        _bundledCatalogPath = Path.Combine(baseDirectory, "Data", "dock-catalog.json");
        _localCatalogPath = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "HpDockFirmware",
            "Catalogs",
            "dock-catalog.json");
    }

    public async Task<FirmwareCatalog> LoadAsync(CancellationToken cancellationToken = default)
    {
        var activePath = File.Exists(_localCatalogPath) ? _localCatalogPath : _bundledCatalogPath;
        await using var stream = File.OpenRead(activePath);
        var catalog = await JsonSerializer.DeserializeAsync<FirmwareCatalog>(stream, _serializerOptions, cancellationToken);

        return catalog ?? new FirmwareCatalog();
    }

    public async Task SaveAsync(FirmwareCatalog catalog, CancellationToken cancellationToken = default)
    {
        var directory = Path.GetDirectoryName(_localCatalogPath)!;
        Directory.CreateDirectory(directory);

        await using var stream = File.Create(_localCatalogPath);
        await JsonSerializer.SerializeAsync(stream, catalog, _serializerOptions, cancellationToken);
    }

    public string GetLocalCatalogPath() => _localCatalogPath;

    public string GetBundledCatalogPath() => _bundledCatalogPath;

    public bool HasLocalCatalog() => File.Exists(_localCatalogPath);

    public FirmwarePackageInfo? MatchPackage(DockDeviceInfo? dock, FirmwareCatalog catalog)
    {
        if (dock is null)
        {
            return null;
        }

        return catalog.Packages.FirstOrDefault(pkg =>
            (!string.IsNullOrWhiteSpace(pkg.ProductId) && string.Equals(pkg.ProductId, dock.ProductId, StringComparison.OrdinalIgnoreCase))
            || (!string.IsNullOrWhiteSpace(pkg.DetectPattern) && dock.DisplayName.Contains(pkg.DetectPattern, StringComparison.OrdinalIgnoreCase))
            || dock.ModelName.Contains(pkg.DockModel, StringComparison.OrdinalIgnoreCase));
    }
}
