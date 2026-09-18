param(
    [string]$Configuration = "Release",
    [bool]$SelfContained = $true
)

$ErrorActionPreference = "Stop"
$root = Split-Path -Parent $PSScriptRoot
$dist = Join-Path $root "dist\WindowsAdmin"
$webProject = Join-Path $root "src\WinAdmin.Web\WinAdmin.Web.csproj"
$desktopProject = Join-Path $root "src\WinAdmin.Desktop\WinAdmin.Desktop.csproj"
$portalOut = Join-Path $dist "portal"
$zip = Join-Path $root "dist\WindowsAdmin-win-x64.zip"

if (Test-Path $dist) {
    Remove-Item -Recurse -Force $dist
}

$runtimeArgs = @(
    "-c", $Configuration,
    "-r", "win-x64",
    "-p:DebugType=None",
    "-p:DebugSymbols=false"
)
if ($SelfContained) {
    $runtimeArgs += @("--self-contained", "true", "-p:PublishSingleFile=false", "-p:IncludeNativeLibrariesForSelfExtract=true")
} else {
    $runtimeArgs += @("--self-contained", "false")
}

dotnet publish $desktopProject @runtimeArgs -o $dist
if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }

dotnet publish $webProject @runtimeArgs -o $portalOut
if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }

Get-ChildItem -Path $dist -Recurse -Include *.pdb, *.xml | Remove-Item -Force -ErrorAction SilentlyContinue

$exe = Join-Path $dist "WindowsAdmin.exe"
$portalExe = Join-Path $portalOut "WinAdmin.Web.exe"
if (-not (Test-Path $exe)) {
    throw "Expected $exe after publish."
}
if (-not (Test-Path $portalExe)) {
    throw "Expected $portalExe after publish."
}

if (Test-Path $zip) {
    Remove-Item -Force $zip
}
Compress-Archive -Path $dist -DestinationPath $zip -CompressionLevel Optimal

$size = [math]::Round((Get-ChildItem $dist -Recurse | Measure-Object Length -Sum).Sum / 1MB, 1)
$zipSize = [math]::Round((Get-Item $zip).Length / 1MB, 1)

Write-Host "Published: $exe"
Write-Host "Portal:    $portalExe"
Write-Host "Folder:    $dist  ($size MB)"
Write-Host "Zip:       $zip  ($zipSize MB)"
Write-Host "Copy the WindowsAdmin folder (or the zip) to another Windows 64-bit PC and run WindowsAdmin.exe"
