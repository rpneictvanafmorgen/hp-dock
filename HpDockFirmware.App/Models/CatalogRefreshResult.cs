namespace HpDockFirmware.App.Models;

public sealed class CatalogRefreshResult
{
    public required FirmwareCatalog Catalog { get; init; }
    public int UpdatedCount { get; init; }
    public int FailedCount { get; init; }
    public string StatusMessage { get; init; } = string.Empty;
}
