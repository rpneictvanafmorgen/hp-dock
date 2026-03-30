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
    private readonly FirmwareDownloadService _firmwareDownloadService;
    private readonly SoftPaqExtractionService _softPaqExtractionService;
    private readonly ProcessRunnerService _processRunnerService;
    private readonly LogService _logService;

    private FirmwareCatalog _catalog = new();
    private DockDetectionSnapshot _lastSnapshot = new();
    private DockDeviceInfo? _selectedDock;
    private FirmwarePackageInfo? _recommendedPackage;
    private FirmwarePackageInfo? _selectedManualPackage;
    private string? _installerPath;
    private string _statusMessage = "Ready.";
    private string? _postInstallVerificationSummary;
    private bool _isBusy;

    public MainViewModel(
        DockDetectionService dockDetectionService,
        FirmwareCatalogService catalogService,
        HpCatalogRefreshService catalogRefreshService,
        FirmwareDownloadService firmwareDownloadService,
        SoftPaqExtractionService softPaqExtractionService,
        ProcessRunnerService processRunnerService,
        LogService logService)
    {
        _dockDetectionService = dockDetectionService;
        _catalogService = catalogService;
        _catalogRefreshService = catalogRefreshService;
        _firmwareDownloadService = firmwareDownloadService;
        _softPaqExtractionService = softPaqExtractionService;
        _processRunnerService = processRunnerService;
        _logService = logService;

        RefreshCommand = new AsyncRelayCommand(RefreshAsync, () => !IsBusy);
        ExportDiagnosticsCommand = new AsyncRelayCommand(ExportDiagnosticsAsync, () => !IsBusy);
        DownloadPackageCommand = new AsyncRelayCommand(DownloadPackageAsync, CanDownloadPackage);
        BrowseInstallerCommand = new RelayCommand(BrowseInstaller, () => !IsBusy);
        ClearManualSelectionCommand = new RelayCommand(ClearManualSelection, () => !IsBusy && SelectedManualPackage is not null);
        InstallCommand = new AsyncRelayCommand(InstallAsync, CanInstall);
        OpenLogDirectoryCommand = new RelayCommand(OpenLogDirectory);
    }

    public ObservableCollection<DockDeviceInfo> Docks { get; } = [];
    public ObservableCollection<DockDetectionCandidate> DiagnosticCandidates { get; } = [];
    public ObservableCollection<FirmwarePackageInfo> ManualPackageOptions { get; } = [];

    public AsyncRelayCommand RefreshCommand { get; }
    public AsyncRelayCommand ExportDiagnosticsCommand { get; }
    public AsyncRelayCommand DownloadPackageCommand { get; }
    public RelayCommand BrowseInstallerCommand { get; }
    public RelayCommand ClearManualSelectionCommand { get; }
    public AsyncRelayCommand InstallCommand { get; }
    public RelayCommand OpenLogDirectoryCommand { get; }

    public DockDeviceInfo? SelectedDock
    {
        get => _selectedDock;
        set
        {
            if (SetProperty(ref _selectedDock, value))
            {
                SyncRecommendedPackage();
                RaisePropertyChanged(nameof(CurrentFirmwareVersion));
                RaisePropertyChanged(nameof(UpdateStatus));
                RaisePropertyChanged(nameof(UpdateStatusDetail));
                RaisePropertyChanged(nameof(FirmwareVersionSummary));
                RaisePropertyChanged(nameof(InstallHelpText));
            }
        }
    }

    public FirmwarePackageInfo? SelectedManualPackage
    {
        get => _selectedManualPackage;
        set
        {
            if (SetProperty(ref _selectedManualPackage, value))
            {
                SyncRecommendedPackage();
                RaisePropertyChanged(nameof(IsManualSelectionActive));
                RaisePropertyChanged(nameof(ManualSelectionStatus));
                NotifyCommands();
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
                RaisePropertyChanged(nameof(TargetFirmwareVersion));
                RaisePropertyChanged(nameof(RecommendedPackageNotes));
                RaisePropertyChanged(nameof(DownloadUrl));
                RaisePropertyChanged(nameof(InstallerArguments));
                RaisePropertyChanged(nameof(UpdateStatus));
                RaisePropertyChanged(nameof(UpdateStatusDetail));
                RaisePropertyChanged(nameof(FirmwareVersionSummary));
                RaisePropertyChanged(nameof(InstallHelpText));
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
                RaisePropertyChanged(nameof(InstallHelpText));
                NotifyCommands();
            }
        }
    }

    public string InstallerArguments => RecommendedPackage?.InstallerArguments ?? string.Empty;
    public string RecommendedPackageName => RecommendedPackage?.PackageDisplayName ?? "No catalog match";
    public string RecommendedPackageVersion => RecommendedPackage?.Version ?? "Unknown";
    public string CurrentFirmwareVersion => string.IsNullOrWhiteSpace(SelectedDock?.FirmwareVersion) ? "Not reported by dock" : SelectedDock!.FirmwareVersion!;
    public string TargetFirmwareVersion => RecommendedPackage is null ? "Unknown" : _firmwareDownloadService.ResolveFirmwareVersion(RecommendedPackage);
    public bool IsManualSelectionActive => SelectedManualPackage is not null;
    public string ManualSelectionStatus => SelectedManualPackage is null
        ? "Using detected dock model."
        : $"Manual override: {SelectedManualPackage.PackageDisplayName}";
    public string UpdateStatus => BuildUpdateStatus();
    public string UpdateStatusDetail => BuildUpdateStatusDetail();
    public string RecommendedPackageNotes => RecommendedPackage?.Notes ?? "Refresh the catalog from HP or adjust the bundled source definitions for your exact dock models.";
    public string? DownloadUrl => RecommendedPackage is null ? null : _firmwareDownloadService.ResolveDownloadUrl(RecommendedPackage);
    public string FirmwareVersionSummary => BuildFirmwareVersionSummary();
    public string InstallHelpText => SelectedDock is null
        ? "Select a detected dock first."
        : string.IsNullOrWhiteSpace(InstallerPath)
            ? "Browse to the HP updater .exe, or download the mapped package first."
            : File.Exists(InstallerPath)
                ? "Ready to run the HP updater."
                : "The selected installer path does not exist.";
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
            _postInstallVerificationSummary = null;
            RaisePropertyChanged(nameof(UpdateStatusDetail));
            await CaptureAndApplySnapshotAsync();
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

            ApplySnapshot(_lastSnapshot, SelectedDock);
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

    private async Task DownloadPackageAsync()
    {
        if (RecommendedPackage is null)
        {
            return;
        }

        try
        {
            IsBusy = true;
            StatusMessage = "Downloading firmware package...";

            var resolvedUrl = _firmwareDownloadService.ResolveDownloadUrl(RecommendedPackage);
            if (string.IsNullOrWhiteSpace(resolvedUrl))
            {
                throw new InvalidOperationException("No downloadable package URL is available for this dock.");
            }

            var fileName = GetPackageFileName(RecommendedPackage, resolvedUrl);
            var downloadedPath = await _firmwareDownloadService.DownloadAsync(resolvedUrl, fileName);
            InstallerPath = downloadedPath;
            StatusMessage = $"Firmware package downloaded to {downloadedPath}";
        }
        catch (Exception ex)
        {
            StatusMessage = $"Download failed: {ex.Message}";
            MessageBox.Show(ex.Message, "Download error", MessageBoxButton.OK, MessageBoxImage.Error);
        }
        finally
        {
            IsBusy = false;
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
            var previousDock = SelectedDock;
            var previousFirmwareVersion = CurrentFirmwareVersion;
            StatusMessage = "Preparing firmware package...";

            var preparedInstallerPath = await _softPaqExtractionService.PrepareInstallerAsync(installerPath);

            StatusMessage = "Running HP firmware installer...";

            var logFile = _logService.CreateLogFilePath();
            var result = await _processRunnerService.RunInstallerAsync(preparedInstallerPath, InstallerArguments, logFile);

            string? verificationSummary = null;
            if (result.Success)
            {
                StatusMessage = "Installer finished. Verifying dock firmware...";
                verificationSummary = await VerifyFirmwareAfterInstallAsync(previousDock, previousFirmwareVersion);
            }

            StatusMessage = verificationSummary is null
                ? result.Summary
                : $"{result.Summary} {verificationSummary}";
            var detail = verificationSummary is null
                ? $"{result.Summary}\n\nCommand: {result.CommandLine}\nLog: {result.LogFilePath}"
                : $"{result.Summary}\n{verificationSummary}\n\nCommand: {result.CommandLine}\nLog: {result.LogFilePath}";
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
        && !string.IsNullOrWhiteSpace(InstallerPath)
        && File.Exists(InstallerPath);

    private bool CanDownloadPackage() =>
        !IsBusy
        && SelectedDock is not null
        && RecommendedPackage is not null
        && !string.IsNullOrWhiteSpace(_firmwareDownloadService.ResolveDownloadUrl(RecommendedPackage));

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
        UpdateManualPackageOptions();
        if (SelectedManualPackage is not null)
        {
            SelectedManualPackage = _catalog.Packages.FirstOrDefault(p => string.Equals(p.Id, SelectedManualPackage.Id, StringComparison.OrdinalIgnoreCase));
        }
        else
        {
            SyncRecommendedPackage();
        }
        RaisePropertyChanged(nameof(CatalogSummary));
    }

    private async Task CaptureAndApplySnapshotAsync(DockDeviceInfo? preferredDock = null)
    {
        _lastSnapshot = await _dockDetectionService.CaptureSnapshotAsync();
        ApplySnapshot(_lastSnapshot, preferredDock);
    }

    private void ApplySnapshot(DockDetectionSnapshot snapshot, DockDeviceInfo? preferredDock = null)
    {
        Docks.Clear();
        foreach (var dock in snapshot.DetectedDocks)
        {
            Docks.Add(dock);
        }

        DiagnosticCandidates.Clear();
        foreach (var candidate in snapshot.Candidates)
        {
            DiagnosticCandidates.Add(candidate);
        }

        var matchedDock = FindMatchingDock(snapshot.DetectedDocks, preferredDock) ?? snapshot.DetectedDocks.FirstOrDefault();
        SelectedDock = matchedDock;
        RaisePropertyChanged(nameof(DiagnosticsSummary));
        RaisePropertyChanged(nameof(DetectionAdvisory));
        RaisePropertyChanged(nameof(UpdateStatus));
        RaisePropertyChanged(nameof(UpdateStatusDetail));
        RaisePropertyChanged(nameof(FirmwareVersionSummary));
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
        DownloadPackageCommand.NotifyCanExecuteChanged();
        BrowseInstallerCommand.NotifyCanExecuteChanged();
        ClearManualSelectionCommand.NotifyCanExecuteChanged();
        InstallCommand.NotifyCanExecuteChanged();
    }

    private static string GetPackageFileName(FirmwarePackageInfo package, string? resolvedUrl = null)
    {
        var url = resolvedUrl ?? package.DownloadUrl;
        if (Uri.TryCreate(url, UriKind.Absolute, out var uri))
        {
            var fileName = Path.GetFileName(uri.LocalPath);
            if (!string.IsNullOrWhiteSpace(fileName))
            {
                return fileName;
            }
        }

        return package.InstallerFileName;
    }

    private async Task<string> VerifyFirmwareAfterInstallAsync(DockDeviceInfo previousDock, string previousFirmwareVersion)
    {
        await Task.Delay(TimeSpan.FromSeconds(5));
        await CaptureAndApplySnapshotAsync(previousDock);

        var currentDock = SelectedDock;
        if (currentDock is null)
        {
            _postInstallVerificationSummary = "Post-install verification could not find the dock after the installer finished.";
            RaisePropertyChanged(nameof(UpdateStatusDetail));
            return _postInstallVerificationSummary;
        }

        var currentFirmwareVersion = CurrentFirmwareVersion;
        var targetVersion = TargetFirmwareVersion;
        _postInstallVerificationSummary = BuildPostInstallVerificationSummary(previousFirmwareVersion, currentFirmwareVersion, targetVersion);
        RaisePropertyChanged(nameof(UpdateStatusDetail));
        return _postInstallVerificationSummary;
    }

    private string BuildPostInstallVerificationSummary(string previousFirmwareVersion, string currentFirmwareVersion, string targetVersion)
    {
        if (currentFirmwareVersion.Equals("Not reported by dock", StringComparison.OrdinalIgnoreCase))
        {
            return "Post-install verification could not read the dock firmware version.";
        }

        if (NormalizeVersion(currentFirmwareVersion).Equals(NormalizeVersion(targetVersion), StringComparison.OrdinalIgnoreCase))
        {
            return $"Post-install verification confirmed firmware {currentFirmwareVersion}.";
        }

        if (!NormalizeVersion(previousFirmwareVersion).Equals(NormalizeVersion(currentFirmwareVersion), StringComparison.OrdinalIgnoreCase))
        {
            return $"Post-install verification saw the dock change from {previousFirmwareVersion} to {currentFirmwareVersion}.";
        }

        return $"Post-install verification still reads firmware {currentFirmwareVersion}.";
    }

    private string BuildFirmwareVersionSummary()
    {
        if (SelectedDock is null)
        {
            return "Select a detected dock to compare installed and target firmware.";
        }

        if (RecommendedPackage is null)
        {
            return "No catalog package matched this dock, so there is no target firmware version to compare.";
        }

        var current = CurrentFirmwareVersion;
        var target = TargetFirmwareVersion;
        if (current.Equals("Not reported by dock", StringComparison.OrdinalIgnoreCase))
        {
            return $"Target firmware from the selected HP package: {target}. The dock did not report its current firmware version.";
        }

        if (NormalizeVersion(current).Equals(NormalizeVersion(target), StringComparison.OrdinalIgnoreCase))
        {
            return $"The dock already reports the same firmware version as the selected package: {target}.";
        }

        return $"Current dock firmware: {current}. Target firmware from the selected package: {target}.";
    }

    private string BuildUpdateStatus()
    {
        if (SelectedDock is null)
        {
            return "No dock selected";
        }

        if (RecommendedPackage is null)
        {
            return "No package match";
        }

        var current = CurrentFirmwareVersion;
        var target = TargetFirmwareVersion;
        if (current.Equals("Not reported by dock", StringComparison.OrdinalIgnoreCase))
        {
            return "Unknown";
        }

        if (NormalizeVersion(current).Equals(NormalizeVersion(target), StringComparison.OrdinalIgnoreCase))
        {
            return "Up to date";
        }

        return "Update available";
    }

    private string BuildUpdateStatusDetail()
    {
        if (!string.IsNullOrWhiteSpace(_postInstallVerificationSummary))
        {
            return _postInstallVerificationSummary;
        }

        if (SelectedDock is null)
        {
            return "Select a detected dock to evaluate firmware status.";
        }

        if (RecommendedPackage is null)
        {
            return "No catalog package matched this dock yet.";
        }

        var current = CurrentFirmwareVersion;
        var target = TargetFirmwareVersion;
        if (current.Equals("Not reported by dock", StringComparison.OrdinalIgnoreCase))
        {
            return $"The dock did not report its current firmware version. The selected package targets {target}.";
        }

        if (NormalizeVersion(current).Equals(NormalizeVersion(target), StringComparison.OrdinalIgnoreCase))
        {
            return $"The dock already reports firmware {current}, which matches the selected package.";
        }

        return $"The dock currently reports {current}. The selected package targets {target}.";
    }

    private static string NormalizeVersion(string version)
    {
        return new string(version.Where(char.IsLetterOrDigit).ToArray()).ToUpperInvariant();
    }

    private void UpdateManualPackageOptions()
    {
        ManualPackageOptions.Clear();
        foreach (var package in _catalog.Packages.OrderBy(p => p.PackageDisplayName, StringComparer.OrdinalIgnoreCase))
        {
            ManualPackageOptions.Add(package);
        }
        RaisePropertyChanged(nameof(ManualSelectionStatus));
    }

    private void SyncRecommendedPackage()
    {
        RecommendedPackage = SelectedManualPackage ?? _catalogService.MatchPackage(SelectedDock, _catalog);
        if (RecommendedPackage is not null && string.IsNullOrWhiteSpace(InstallerPath))
        {
            AutoFillInstallerPath();
        }
    }

    private void ClearManualSelection()
    {
        SelectedManualPackage = null;
    }

    private static DockDeviceInfo? FindMatchingDock(IReadOnlyList<DockDeviceInfo> docks, DockDeviceInfo? preferredDock)
    {
        if (preferredDock is null)
        {
            return null;
        }

        return docks.FirstOrDefault(d =>
            (!string.IsNullOrWhiteSpace(preferredDock.DeviceInstanceId)
             && string.Equals(d.DeviceInstanceId, preferredDock.DeviceInstanceId, StringComparison.OrdinalIgnoreCase))
            || (!string.IsNullOrWhiteSpace(preferredDock.ProductId)
                && string.Equals(d.ProductId, preferredDock.ProductId, StringComparison.OrdinalIgnoreCase)
                && string.Equals(d.DisplayName, preferredDock.DisplayName, StringComparison.OrdinalIgnoreCase))
            || (!string.IsNullOrWhiteSpace(preferredDock.SerialNumber)
                && string.Equals(d.SerialNumber, preferredDock.SerialNumber, StringComparison.OrdinalIgnoreCase)));
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
