using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Text;
using System.Windows;
using HpDockFirmware.App.Models;
using HpDockFirmware.App.Services;
using Microsoft.Win32;

namespace HpDockFirmware.App.ViewModels;

public sealed class MainViewModel : ObservableObject
{
    private static readonly TimeSpan AutomaticCatalogRefreshAge = TimeSpan.FromDays(7);

    private readonly DockDetectionService _dockDetectionService;
    private readonly FirmwareCatalogService _catalogService;
    private readonly HpCatalogRefreshService _catalogRefreshService;
    private readonly ProcessRunnerService _processRunnerService;
    private readonly LogService _logService;

    private FirmwareCatalog _catalog = new();
    private DockDetectionSnapshot _lastSnapshot = new();
    private DockDeviceInfo? _selectedDock;
    private FirmwarePackageInfo? _recommendedPackage;
    private string? _installerPath;
    private string _statusMessage = "Ready.";
    private bool _isBusy;

    public MainViewModel(
        DockDetectionService dockDetectionService,
        FirmwareCatalogService catalogService,
        HpCatalogRefreshService catalogRefreshService,
        ProcessRunnerService processRunnerService,
        LogService logService)
    {
        _dockDetectionService = dockDetectionService;
        _catalogService = catalogService;
        _catalogRefreshService = catalogRefreshService;
        _processRunnerService = processRunnerService;
        _logService = logService;

        RefreshCommand = new AsyncRelayCommand(RefreshAsync, () => !IsBusy);
        ExportDiagnosticsCommand = new AsyncRelayCommand(ExportDiagnosticsAsync, () => !IsBusy);
        BrowseInstallerCommand = new RelayCommand(BrowseInstaller, () => !IsBusy);
        InstallCommand = new AsyncRelayCommand(InstallAsync, CanInstall);
        OpenLogDirectoryCommand = new RelayCommand(OpenLogDirectory);
    }

    public ObservableCollection<DockDeviceInfo> Docks { get; } = [];
    public ObservableCollection<DockDetectionCandidate> DiagnosticCandidates { get; } = [];

    public AsyncRelayCommand RefreshCommand { get; }
    public AsyncRelayCommand ExportDiagnosticsCommand { get; }
    public RelayCommand BrowseInstallerCommand { get; }
    public AsyncRelayCommand InstallCommand { get; }
    public RelayCommand OpenLogDirectoryCommand { get; }

    public DockDeviceInfo? SelectedDock
    {
        get => _selectedDock;
        set
        {
            if (SetProperty(ref _selectedDock, value))
            {
                RecommendedPackage = _catalogService.MatchPackage(value, _catalog);
                if (RecommendedPackage is not null && string.IsNullOrWhiteSpace(InstallerPath))
                {
                    AutoFillInstallerPath();
                }
            }
        }
    }

    public FirmwarePackageInfo? RecommendedPackage
    {
        get => _recommendedPackage;
        private set
        {
            if (SetProperty(ref _recommendedPackage, value))
            {
                RaisePropertyChanged(nameof(RecommendedPackageName));
                RaisePropertyChanged(nameof(RecommendedPackageVersion));
                RaisePropertyChanged(nameof(RecommendedPackageNotes));
                RaisePropertyChanged(nameof(DownloadUrl));
                RaisePropertyChanged(nameof(InstallerArguments));
                NotifyCommands();
            }
        }
    }

    public string? InstallerPath
    {
        get => _installerPath;
        set
        {
            if (SetProperty(ref _installerPath, value))
            {
                NotifyCommands();
            }
        }
    }

    public string InstallerArguments => RecommendedPackage?.InstallerArguments ?? string.Empty;
    public string RecommendedPackageName => RecommendedPackage?.PackageDisplayName ?? "No catalog match";
    public string RecommendedPackageVersion => RecommendedPackage?.Version ?? "Unknown";
    public string RecommendedPackageNotes => RecommendedPackage?.Notes ?? "Refresh the catalog from HP or adjust the bundled source definitions for your exact dock models.";
    public string? DownloadUrl => RecommendedPackage?.DownloadUrl;
    public string CatalogSummary => _catalog.GeneratedAtUtc is null
        ? $"Using bundled catalog: {_catalogService.GetBundledCatalogPath()}"
        : $"Catalog updated {_catalog.GeneratedAtUtc:yyyy-MM-dd HH:mm} UTC from {_catalog.GeneratedFrom}";
    public string DiagnosticsSummary => DiagnosticCandidates.Count == 0
        ? "No dock-like device candidates captured yet."
        : $"Captured {DiagnosticCandidates.Count} dock-related candidate device(s).";
    public string DetectionAdvisory => _lastSnapshot.Advisory ?? string.Empty;

    public string StatusMessage
    {
        get => _statusMessage;
        private set => SetProperty(ref _statusMessage, value);
    }

    public bool IsBusy
    {
        get => _isBusy;
        private set
        {
            if (SetProperty(ref _isBusy, value))
            {
                NotifyCommands();
            }
        }
    }

    public string AppVersion => Assembly.GetExecutingAssembly().GetName().Version?.ToString(3) ?? "1.0";

    public async Task InitializeAsync()
    {
        UpdateCatalogState(await _catalogService.LoadAsync());
        await RefreshAsync();

        if (ShouldAutomaticallyRefreshCatalog(_catalog))
        {
            await RefreshCatalogAsync(showDialog: false);
        }
    }

    private async Task RefreshAsync()
    {
        try
        {
            IsBusy = true;
            StatusMessage = "Detecting connected HP docks...";

            UpdateCatalogState(await _catalogService.LoadAsync());
            _lastSnapshot = await _dockDetectionService.CaptureSnapshotAsync();
            var detected = _lastSnapshot.DetectedDocks;

            Docks.Clear();
            foreach (var dock in detected)
            {
                Docks.Add(dock);
            }

            DiagnosticCandidates.Clear();
            foreach (var candidate in _lastSnapshot.Candidates)
            {
                DiagnosticCandidates.Add(candidate);
            }

            SelectedDock = Docks.FirstOrDefault();
            RaisePropertyChanged(nameof(DiagnosticsSummary));
            RaisePropertyChanged(nameof(DetectionAdvisory));
            StatusMessage = Docks.Count > 0
                ? $"Detected {Docks.Count} HP dock device(s)."
                : DiagnosticCandidates.Count > 0
                    ? $"No confirmed dock model detected, but found {DiagnosticCandidates.Count} dock-related candidate device(s)."
                    : "No supported HP dock detected. Connect the dock and click Refresh.";
        }
        catch (Exception ex)
        {
            StatusMessage = $"Detection failed: {ex.Message}";
            MessageBox.Show(ex.Message, "Detection error", MessageBoxButton.OK, MessageBoxImage.Error);
        }
        finally
        {
            IsBusy = false;
        }
    }

    private async Task ExportDiagnosticsAsync()
    {
        try
        {
            IsBusy = true;
            StatusMessage = "Exporting diagnostics report...";

            _lastSnapshot = await _dockDetectionService.CaptureSnapshotAsync();
            var reportPath = _logService.CreateDiagnosticsFilePath();
            var report = BuildDiagnosticsReport(_lastSnapshot);
            await File.WriteAllTextAsync(reportPath, report);

            DiagnosticCandidates.Clear();
            foreach (var candidate in _lastSnapshot.Candidates)
            {
                DiagnosticCandidates.Add(candidate);
            }

            RaisePropertyChanged(nameof(DiagnosticsSummary));
            RaisePropertyChanged(nameof(DetectionAdvisory));
            StatusMessage = $"Diagnostics exported to {reportPath}";
            MessageBox.Show(
                $"Diagnostics report written to:\n{reportPath}",
                "Diagnostics exported",
                MessageBoxButton.OK,
                MessageBoxImage.Information);
        }
        catch (Exception ex)
        {
            StatusMessage = $"Diagnostics export failed: {ex.Message}";
            MessageBox.Show(ex.Message, "Diagnostics error", MessageBoxButton.OK, MessageBoxImage.Error);
        }
        finally
        {
            IsBusy = false;
        }
    }

    private async Task RefreshCatalogAsync(bool showDialog)
    {
        try
        {
            IsBusy = true;
            StatusMessage = "Refreshing catalog from HP sources...";

            var refreshResult = await _catalogRefreshService.RefreshAsync();
            await _catalogService.SaveAsync(refreshResult.Catalog);
            UpdateCatalogState(refreshResult.Catalog);

            RecommendedPackage = _catalogService.MatchPackage(SelectedDock, _catalog);
            StatusMessage = refreshResult.StatusMessage;

            if (showDialog)
            {
                MessageBox.Show(
                    $"{refreshResult.StatusMessage}\n\nSaved catalog: {_catalogService.GetLocalCatalogPath()}",
                    "Catalog refresh completed",
                    MessageBoxButton.OK,
                    refreshResult.FailedCount == 0 ? MessageBoxImage.Information : MessageBoxImage.Warning);
            }
        }
        catch (Exception ex)
        {
            StatusMessage = $"Catalog refresh failed: {ex.Message}";
            if (showDialog)
            {
                MessageBox.Show(ex.Message, "Catalog refresh error", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }
        finally
        {
            IsBusy = false;
        }
    }

    private void BrowseInstaller()
    {
        var dialog = new OpenFileDialog
        {
            Title = "Select HP firmware installer",
            Filter = "Executables (*.exe)|*.exe|All files (*.*)|*.*",
            CheckFileExists = true,
            Multiselect = false
        };

        if (dialog.ShowDialog() == true)
        {
            InstallerPath = dialog.FileName;
            StatusMessage = "Installer selected.";
        }
    }

    private async Task InstallAsync()
    {
        if (SelectedDock is null || string.IsNullOrWhiteSpace(InstallerPath))
        {
            return;
        }

        var installerPath = InstallerPath!;
        if (!File.Exists(installerPath))
        {
            MessageBox.Show("The selected installer file does not exist.", "Installer missing", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        var confirmation = MessageBox.Show(
            "Firmware updates can temporarily disconnect the dock. Save work on any attached devices before continuing.",
            "Install firmware",
            MessageBoxButton.OKCancel,
            MessageBoxImage.Warning);

        if (confirmation != MessageBoxResult.OK)
        {
            return;
        }

        try
        {
            IsBusy = true;
            StatusMessage = "Running HP firmware installer...";

            var logFile = _logService.CreateLogFilePath();
            var result = await _processRunnerService.RunInstallerAsync(installerPath, InstallerArguments, logFile);

            StatusMessage = result.Summary;
            var detail = $"{result.Summary}\n\nCommand: {result.CommandLine}\nLog: {result.LogFilePath}";
            MessageBox.Show(
                detail,
                result.Success ? "Firmware install completed" : "Firmware install finished with warnings",
                MessageBoxButton.OK,
                result.Success ? MessageBoxImage.Information : MessageBoxImage.Warning);
        }
        catch (Exception ex)
        {
            StatusMessage = $"Install failed: {ex.Message}";
            MessageBox.Show(ex.Message, "Install error", MessageBoxButton.OK, MessageBoxImage.Error);
        }
        finally
        {
            IsBusy = false;
        }
    }

    private bool CanInstall() =>
        !IsBusy
        && SelectedDock is not null
        && !string.IsNullOrWhiteSpace(InstallerPath);

    private void AutoFillInstallerPath()
    {
        if (RecommendedPackage is null)
        {
            return;
        }

        var candidate = Path.Combine(AppContext.BaseDirectory, "Packages", RecommendedPackage.InstallerFileName);
        if (File.Exists(candidate))
        {
            InstallerPath = candidate;
        }
    }

    private void OpenLogDirectory()
    {
        var logPath = _logService.CreateLogFilePath();
        var directory = Path.GetDirectoryName(logPath)!;
        Directory.CreateDirectory(directory);

        Process.Start(new ProcessStartInfo
        {
            FileName = "explorer.exe",
            Arguments = $"\"{directory}\"",
            UseShellExecute = true
        });
    }

    private void UpdateCatalogState(FirmwareCatalog catalog)
    {
        _catalog = catalog;
        RaisePropertyChanged(nameof(CatalogSummary));
    }

    private bool ShouldAutomaticallyRefreshCatalog(FirmwareCatalog catalog)
    {
        if (!_catalogService.HasLocalCatalog())
        {
            return true;
        }

        if (catalog.GeneratedAtUtc is null)
        {
            return true;
        }

        return DateTimeOffset.UtcNow - catalog.GeneratedAtUtc.Value >= AutomaticCatalogRefreshAge;
    }

    private void NotifyCommands()
    {
        RefreshCommand.NotifyCanExecuteChanged();
        ExportDiagnosticsCommand.NotifyCanExecuteChanged();
        BrowseInstallerCommand.NotifyCanExecuteChanged();
        InstallCommand.NotifyCanExecuteChanged();
    }

    private string BuildDiagnosticsReport(DockDetectionSnapshot snapshot)
    {
        var builder = new StringBuilder();
        builder.AppendLine("HP Dock Firmware Utility Diagnostics");
        builder.AppendLine($"Generated: {DateTimeOffset.Now:O}");
        builder.AppendLine();
        builder.AppendLine("Detected docks");
        builder.AppendLine("--------------");

        if (snapshot.DetectedDocks.Count == 0)
        {
            builder.AppendLine("None");
        }
        else
        {
            foreach (var dock in snapshot.DetectedDocks)
            {
                builder.AppendLine($"Model: {dock.DisplayName}");
                builder.AppendLine($"Source: {dock.Source}");
                builder.AppendLine($"Product ID: {dock.ProductId}");
                builder.AppendLine($"Firmware: {dock.FirmwareVersion}");
                builder.AppendLine($"Serial: {dock.SerialNumber}");
                builder.AppendLine($"Instance ID: {dock.DeviceInstanceId}");
                builder.AppendLine($"Hardware ID: {dock.HardwareId}");
                builder.AppendLine();
            }
        }

        builder.AppendLine("Detection candidates");
        builder.AppendLine("--------------------");

        if (snapshot.Candidates.Count == 0)
        {
            builder.AppendLine("None");
        }
        else
        {
            foreach (var candidate in snapshot.Candidates)
            {
                builder.AppendLine($"Name: {candidate.Name}");
                builder.AppendLine($"Source: {candidate.Source}");
                builder.AppendLine($"Manufacturer: {candidate.Manufacturer}");
                builder.AppendLine($"Class: {candidate.DeviceClass}");
                builder.AppendLine($"Instance ID: {candidate.DeviceInstanceId}");
                builder.AppendLine($"Hardware ID: {candidate.HardwareId}");
                builder.AppendLine($"Reason: {candidate.Reason}");
                builder.AppendLine();
            }
        }

        builder.AppendLine("Catalog summary");
        builder.AppendLine("---------------");
        builder.AppendLine(CatalogSummary);
        builder.AppendLine($"Local catalog path: {_catalogService.GetLocalCatalogPath()}");
        if (!string.IsNullOrWhiteSpace(snapshot.Advisory))
        {
            builder.AppendLine($"Advisory: {snapshot.Advisory}");
        }
        builder.AppendLine();
        builder.AppendLine("Raw inventory");
        builder.AppendLine("-------------");

        if (snapshot.RawInventory.Count == 0)
        {
            builder.AppendLine("None");
        }
        else
        {
            foreach (var device in snapshot.RawInventory)
            {
                builder.AppendLine($"Name: {device.Name}");
                builder.AppendLine($"Manufacturer: {device.Manufacturer}");
                builder.AppendLine($"Class: {device.DeviceClass}");
                builder.AppendLine($"Instance ID: {device.DeviceInstanceId}");
                builder.AppendLine($"Hardware ID: {device.HardwareId}");
                builder.AppendLine($"Details: {device.Reason}");
                builder.AppendLine();
            }
        }

        return builder.ToString();
    }
}
