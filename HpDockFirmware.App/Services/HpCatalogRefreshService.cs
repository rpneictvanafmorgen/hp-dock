using System.IO;
using System.Net.Http;
using System.Net;
using System.Text.Json;
using System.Text.RegularExpressions;
using HpDockFirmware.App.Models;

namespace HpDockFirmware.App.Services;

public sealed class HpCatalogRefreshService
{
    private static readonly Regex SoftPaqLinkRegex = new(@"https://ftp(?:\.ext)?\.hp\.com/pub/softpaq/[^""'\s<>]+/sp(?<softpaq>\d{5,6})\.exe", RegexOptions.IgnoreCase | RegexOptions.Compiled);
    private static readonly Regex SupportDetailsRegex = new(@"https://support\.hp\.com/[^""'\s<>]+/swdetails/[^""'\s<>]+", RegexOptions.IgnoreCase | RegexOptions.Compiled);
    private static readonly Regex VersionRegex = new(@"version[:\s]+(?<version>[0-9][0-9A-Za-z\.\- ]{1,40})", RegexOptions.IgnoreCase | RegexOptions.Compiled);
    private static readonly Regex RevisionRegex = new(@"Rev\.\s*(?<revision>[A-Z])", RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private readonly HttpClient _httpClient;
    private readonly string _sourceListPath;

    public HpCatalogRefreshService(string baseDirectory)
    {
        _sourceListPath = Path.Combine(baseDirectory, "Data", "dock-sources.json");
        _httpClient = new HttpClient();
        _httpClient.DefaultRequestHeaders.UserAgent.ParseAdd("Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/136.0 Safari/537.36");
        _httpClient.DefaultRequestHeaders.Accept.ParseAdd("text/html,application/xhtml+xml,application/xml;q=0.9,*/*;q=0.8");
    }

    public async Task<CatalogRefreshResult> RefreshAsync(CancellationToken cancellationToken = default)
    {
        var sourceList = await LoadSourcesAsync(cancellationToken);
        var packages = new List<FirmwarePackageInfo>();
        var updated = 0;
        var failed = 0;

        foreach (var source in sourceList.Sources)
        {
            try
            {
                var package = await BuildPackageAsync(source, cancellationToken);
                packages.Add(package);
                updated++;
            }
            catch (Exception ex)
            {
                failed++;
                packages.Add(new FirmwarePackageInfo
                {
                    Id = source.Id,
                    DockModel = source.DockModel,
                    ProductId = source.ProductId,
                    DetectPattern = source.DetectPattern,
                    Version = "Unavailable",
                    PackageDisplayName = source.PackageDisplayName,
                    InstallerFileName = source.InstallerFileName,
                    InstallerArguments = source.InstallerArguments,
                    DownloadUrl = source.SourceUrl,
                    SourceUrl = source.SourceUrl,
                    SourceStatus = $"Failed to parse HP source: {ex.Message}",
                    LastCheckedUtc = DateTimeOffset.UtcNow,
                    Notes = source.Notes
                });
            }
        }

        var catalog = new FirmwareCatalog
        {
            Version = DateTimeOffset.UtcNow.ToString("yyyyMMddHHmmss"),
            GeneratedAtUtc = DateTimeOffset.UtcNow,
            GeneratedFrom = "HP support pages configured in dock-sources.json",
            Packages = packages
        };

        return new CatalogRefreshResult
        {
            Catalog = catalog,
            UpdatedCount = updated,
            FailedCount = failed,
            StatusMessage = failed == 0
                ? $"Catalog refreshed from HP sources. Updated {updated} entries."
                : $"Catalog refreshed with partial results. Updated {updated} entries, {failed} failed."
        };
    }

    private async Task<DockCatalogSourceList> LoadSourcesAsync(CancellationToken cancellationToken)
    {
        await using var stream = File.OpenRead(_sourceListPath);
        var sources = await JsonSerializer.DeserializeAsync<DockCatalogSourceList>(
            stream,
            new JsonSerializerOptions { PropertyNameCaseInsensitive = true },
            cancellationToken);

        return sources ?? new DockCatalogSourceList();
    }

    private async Task<FirmwarePackageInfo> BuildPackageAsync(DockCatalogSource source, CancellationToken cancellationToken)
    {
        var html = await _httpClient.GetStringAsync(source.SourceUrl, cancellationToken);
        var decoded = WebUtility.HtmlDecode(html);
        var compact = Regex.Replace(decoded, @"\s+", " ");

        var link = ExtractBestLink(compact, source.SourceHint);
        var version = ExtractVersion(compact, source.SourceHint);

        return new FirmwarePackageInfo
        {
            Id = source.Id,
            DockModel = source.DockModel,
            ProductId = source.ProductId,
            DetectPattern = source.DetectPattern,
            Version = version,
            PackageDisplayName = source.PackageDisplayName,
            InstallerFileName = source.InstallerFileName,
            InstallerArguments = source.InstallerArguments,
            DownloadUrl = link,
            SourceUrl = source.SourceUrl,
            SourceStatus = "OK",
            LastCheckedUtc = DateTimeOffset.UtcNow,
            Notes = $"{source.Notes} Catalog generated from HP source page."
        };
    }

    private static string ExtractBestLink(string html, string? hint)
    {
        var scopedSegment = ExtractScopedSegment(html, hint);
        var supportMatch = SupportDetailsRegex.Match(scopedSegment);
        if (supportMatch.Success)
        {
            return supportMatch.Value;
        }

        var softPaqMatch = SoftPaqLinkRegex.Match(scopedSegment);
        if (softPaqMatch.Success)
        {
            return softPaqMatch.Value;
        }

        supportMatch = SupportDetailsRegex.Match(html);
        if (supportMatch.Success)
        {
            return supportMatch.Value;
        }

        softPaqMatch = SoftPaqLinkRegex.Match(html);
        if (softPaqMatch.Success)
        {
            return softPaqMatch.Value;
        }

        return string.Empty;
    }

    private static string ExtractVersion(string html, string? hint)
    {
        var scopedSegment = ExtractScopedSegment(html, hint);
        var versionMatch = VersionRegex.Match(scopedSegment);
        if (versionMatch.Success)
        {
            return CleanVersion(versionMatch.Groups["version"].Value);
        }

        versionMatch = VersionRegex.Match(html);
        if (versionMatch.Success)
        {
            return CleanVersion(versionMatch.Groups["version"].Value);
        }

        var revisionMatch = RevisionRegex.Match(scopedSegment);
        if (revisionMatch.Success)
        {
            return $"Revision {revisionMatch.Groups["revision"].Value}";
        }

        var softPaqMatch = SoftPaqLinkRegex.Match(scopedSegment);
        if (softPaqMatch.Success)
        {
            return $"SoftPaq sp{softPaqMatch.Groups["softpaq"].Value}";
        }

        return "Unknown";
    }

    private static string ExtractScopedSegment(string html, string? hint)
    {
        if (string.IsNullOrWhiteSpace(hint))
        {
            return html;
        }

        var index = html.IndexOf(hint, StringComparison.OrdinalIgnoreCase);
        if (index < 0)
        {
            return html;
        }

        var start = Math.Max(0, index - 1200);
        var length = Math.Min(html.Length - start, 2600);
        return html.Substring(start, length);
    }

    private static string CleanVersion(string value)
    {
        var normalized = value.Trim().TrimEnd('.', ',', ';', ')');
        return normalized.Length > 60 ? normalized[..60].Trim() : normalized;
    }
}
