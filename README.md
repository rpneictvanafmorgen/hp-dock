# HP Dock Firmware Utility

Windows WPF utility for detecting connected HP docks and launching HP's official firmware installer.

## What it does

- Detects connected HP dock devices through HP WMI when available.
- Falls back to Windows PnP inventory when HP WMI is not present.
- Matches detected docks to a local catalog in `HpDockFirmware.App/Data/dock-catalog.json`.
- Can refresh that catalog from HP source pages configured in `HpDockFirmware.App/Data/dock-sources.json`.
- Lets you choose the staged `HPFirmwareInstaller.exe` package and run it from a GUI.
- Writes installer output to `%LocalAppData%\HpDockFirmware\Logs`.
- Can export a diagnostics report with raw dock-related device candidates to `%LocalAppData%\HpDockFirmware\Logs`.

## Important limitation

This app does not download or reverse-engineer firmware payloads itself. It is an orchestration layer around HP's supported updater workflow. You should stage the correct HP package for your exact dock model and revision.

The in-app catalog refresh uses seeded HP support URLs and HTML parsing heuristics because HP does not expose a clean public dock-catalog API. If HP changes page structure or product URLs, update `dock-sources.json`.

## Customize the catalog

Edit `HpDockFirmware.App/Data/dock-sources.json` if you want the app to generate catalog entries for additional dock models from HP pages.

Edit `HpDockFirmware.App/Data/dock-catalog.json` only if you want to change the bundled fallback catalog.

- `dockModel`: human-readable dock model.
- `productId`: preferred exact match from the device hardware ID.
- `detectPattern`: fallback text match against the detected device name.
- `installerFileName`: defaults to `HPFirmwareInstaller.exe`.
- `installerArguments`: leave empty unless you have validated HP installer arguments for your package.
- `downloadUrl`: informational link shown in the UI.

## Build

```powershell
$env:DOTNET_CLI_HOME="$PWD\.dotnet"
$env:NUGET_PACKAGES="$PWD\.nuget\packages"
dotnet build .\HpDockFirmware.App\HpDockFirmware.App.csproj
```

## Run

```powershell
$env:DOTNET_CLI_HOME="$PWD\.dotnet"
$env:NUGET_PACKAGES="$PWD\.nuget\packages"
dotnet run --project .\HpDockFirmware.App\HpDockFirmware.App.csproj
```
