param(
    [string]$Configuration = "Release"
)

$ErrorActionPreference = "Stop"
$root = Split-Path -Parent $PSScriptRoot
$project = Join-Path $root "src\WinAdmin.Web\WinAdmin.Web.csproj"
$installDir = Join-Path $env:ProgramData "WinAdmin\portal"

dotnet publish $project -c $Configuration -o $installDir
$exe = Join-Path $installDir "WinAdmin.Web.exe"

if (-not (Test-Path $exe)) {
    throw "Published portal not found: $exe"
}

$serviceName = "WinAdminPortal"
$existing = Get-Service -Name $serviceName -ErrorAction SilentlyContinue
if ($existing) {
    Stop-Service $serviceName -Force -ErrorAction SilentlyContinue
    sc.exe delete $serviceName | Out-Null
    Start-Sleep -Seconds 2
}

New-Service -Name $serviceName `
    -BinaryPathName "`"$exe`"" `
    -DisplayName "Windows Admin Portal" `
    -Description "Runs the Windows Admin portal as the system so tasks do not ask for permission." `
    -StartupType Automatic | Out-Null

sc.exe failure $serviceName reset= 86400 actions= restart/3000/restart/5000/restart/5000 | Out-Null
sc.exe sdset $serviceName "D:(A;;CCLCSWRPWPDTLOCRRC;;;SY)(A;;CCDCLCSWRPWPDTLOCRSDRCWDWO;;;BA)(A;;CCLCSWRPWPLOCRRC;;;IU)(A;;RPWP;;;AU)" | Out-Null
Start-Service $serviceName
Write-Host "Service $serviceName is running as LocalSystem. Open http://127.0.0.1:5077"
