$ErrorActionPreference = "Stop"
$serviceName = "WinAdminPortal"
$existing = Get-Service -Name $serviceName -ErrorAction SilentlyContinue
if (-not $existing) {
    Write-Host "Service $serviceName is not installed."
    exit 0
}

Stop-Service $serviceName -Force -ErrorAction SilentlyContinue
sc.exe delete $serviceName | Out-Null
Write-Host "Removed $serviceName."
