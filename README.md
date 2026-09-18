# Windows Admin

A full-window Windows application plus a local web portal for this PC. It can check Windows updates, install apps, clean storage, and build a health report without using a terminal and without popup tools.

## What you get

- **Windows app**: `WinAdmin.Desktop` opens a normal window (not a toast/popup, not a console).
- **Web portal**: the same UI at `http://127.0.0.1:5077` in any browser.
- **Windows Update**: scan, install selected or all updates through the Windows Update Agent.
- **Applications**: list installed programs, search winget, silent install/uninstall.
- **Storage**: drive usage, reclaim temp/update cache/recycle bin, large-folder view.
- **Health report**: score, Defender, memory, disk, services, recent errors, HTML/JSON export.
- **System tasks**: restart/shutdown with cancel, start/stop services, startup programs.

The portal listens on localhost only.

## Run the Windows app (recommended)

1. Close any open **Windows Admin** window.
2. Open File Explorer and double-click this file only:

`c:\App\Windows Admin\dist\WindowsAdmin\WindowsAdmin.exe`

Do not run `WinAdmin.Web.exe`, `WinAdmin.Desktop.exe`, or anything under `bin\Debug`. Those are older copies.

After it opens, the sidebar should include **Search files** and **Performance**. The same UI is also at `http://127.0.0.1:5077`.

To rebuild the EXE after code changes:

```powershell
.\scripts\publish-exe.ps1
.\dist\WindowsAdmin\WindowsAdmin.exe
```

## Run the web portal only

```powershell
dotnet run --project .\src\WinAdmin.Web\WinAdmin.Web.csproj
```

Then open `http://127.0.0.1:5077`. There is no console window when you start the built `.exe`.

## Install as a background Windows service

Build the portal, then run `.\scripts\install-service.ps1` as Administrator. After that, the web portal stays available without opening the desktop app.

Remove it with `.\scripts\uninstall-service.ps1`.

## Requirements

- Windows 10/11
- .NET 9
- Administrator rights for updates, installs, and cleanup
- WebView2 (already present on current Windows 11)
- winget for catalog installs
