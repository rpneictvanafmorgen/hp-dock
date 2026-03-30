using System.IO;
using System.Net.Http;
using HpDockFirmware.App.Models;

namespace HpDockFirmware.App.Services;

public sealed class FirmwareDownloadService
{
    private readonly HttpClient _httpClient = new();

    public FirmwareDownloadService()
    {
        _httpClient.DefaultRequestHeaders.UserAgent.ParseAdd("HPDockFirmwareUtility/1.0");
        _httpClient.Timeout = TimeSpan.FromMinutes(15);
    }

    public string GetDownloadDirectory()
    {
        var directory = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "HpDockFirmware",
            "Packages");
        Directory.CreateDirectory(directory);
        return directory;
    }

    public async Task<string> DownloadAsync(string url, string fileName, CancellationToken cancellationToken = default)
    {
        var targetPath = Path.Combine(GetDownloadDirectory(), fileName);

        using var response = await _httpClient.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        response.EnsureSuccessStatusCode();

        await using var source = await response.Content.ReadAsStreamAsync(cancellationToken);
        await using var destination = File.Create(targetPath);
        await source.CopyToAsync(destination, cancellationToken);

        return targetPath;
    }

    public string ResolveDownloadUrl(FirmwarePackageInfo package)
    {
        if (!string.IsNullOrWhiteSpace(package.DownloadUrl)
            && package.DownloadUrl.Contains("ftp.hp.com", StringComparison.OrdinalIgnoreCase))
        {
            return package.DownloadUrl;
        }

        return package.Id switch
        {
            "hp-usb-c-dock-g5" => "https://ftp.hp.com/pub/softpaq/sp161501-162000/sp161510.exe",
            "hp-thunderbolt-dock-g2" => "https://ftp.hp.com/pub/softpaq/sp153501-154000/sp153722.exe",
            "hp-usb-c-dock-g4" => "https://ftp.hp.com/pub/softpaq/sp88501-89000/sp88999.exe",
            "hp-usb-c-g5-essential-dock" => "https://ftp.hp.com/pub/softpaq/sp158001-158500/sp158026.exe",
            _ => package.DownloadUrl ?? string.Empty
        };
    }
}
