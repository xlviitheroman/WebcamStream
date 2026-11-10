#Requires -RunAsAdministrator
<#
.SYNOPSIS
  Remove the WebcamStream service, scheduled task, and firewall rules.

.EXAMPLE
  .\Uninstall-WebcamStream.ps1
  .\Uninstall-WebcamStream.ps1 -Purge
#>
[CmdletBinding()]
param(
    [string]$InstallDir = $(Join-Path $env:ProgramData "WebcamStream"),
    [switch]$Purge
)

$ErrorActionPreference = "Stop"

function Invoke-Sc {
    param([string]$Arguments)
    $psi = New-Object System.Diagnostics.ProcessStartInfo
    $psi.FileName = "sc.exe"
    $psi.Arguments = $Arguments
    $psi.UseShellExecute = $false
    $psi.RedirectStandardOutput = $true
    $psi.RedirectStandardError = $true
    $p = [System.Diagnostics.Process]::Start($psi)
    $out = $p.StandardOutput.ReadToEnd() + $p.StandardError.ReadToEnd()
    $p.WaitForExit()
    if ($p.ExitCode -ne 0 -and $out -notmatch "1060|1062") {
        throw "sc.exe $Arguments failed ($($p.ExitCode)): $out"
    }
}

Invoke-Sc "stop WebcamStream"
Start-Sleep -Seconds 1
Invoke-Sc "delete WebcamStream"
$task = Get-ScheduledTask -TaskName "WebcamStream" -ErrorAction SilentlyContinue
if ($task) { Unregister-ScheduledTask -TaskName "WebcamStream" -Confirm:$false }
foreach ($name in @("WebcamStream-RTSP-TCP", "WebcamStream-RTP-UDP")) {
    $rule = Get-NetFirewallRule -Name $name -ErrorAction SilentlyContinue
    if ($rule) { Remove-NetFirewallRule -Name $name }
}
Get-Process WebcamStream -ErrorAction SilentlyContinue | Stop-Process -Force
if ($Purge -and (Test-Path -LiteralPath $InstallDir)) {
    Remove-Item -LiteralPath $InstallDir -Recurse -Force
    Write-Host "Removed $InstallDir"
} else {
    Write-Host "Removed the service, scheduled task, and firewall rules."
    Write-Host "Left the files in $InstallDir. Pass -Purge to delete them, including the config."
}
