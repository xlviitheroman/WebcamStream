#Requires -RunAsAdministrator
<#
.SYNOPSIS
  Remove the WebcamStream service, scheduled task, and firewall rules.

.DESCRIPTION
  Camera privacy values written by the installer are left in place. Removing this program
  does not turn the webcam back off.

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
if (Get-Variable -Name PSNativeCommandUseErrorActionPreference -ErrorAction SilentlyContinue) {
    $PSNativeCommandUseErrorActionPreference = $false
}

function Remove-WebcamFirewallRule {
    param([string]$DisplayName)
    $netsh = Join-Path $env:windir "System32\netsh.exe"
    if (-not [Environment]::Is64BitProcess) {
        $native = Join-Path $env:windir "Sysnative\netsh.exe"
        if (Test-Path -LiteralPath $native) { $netsh = $native }
    }
    $psi = New-Object System.Diagnostics.ProcessStartInfo
    $psi.FileName = $netsh
    $psi.Arguments = "advfirewall firewall delete rule name=`"$DisplayName`""
    $psi.UseShellExecute = $false
    $psi.RedirectStandardOutput = $true
    $psi.RedirectStandardError = $true
    $psi.CreateNoWindow = $true
    $p = [System.Diagnostics.Process]::Start($psi)
    $p.StandardOutput.ReadToEnd() | Out-Null
    $p.StandardError.ReadToEnd() | Out-Null
    $p.WaitForExit()
}

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
foreach ($name in @("WebcamStream RTSP", "WebcamStream RTP", "WebcamStream-RTSP-TCP", "WebcamStream-RTP-UDP")) {
    Remove-WebcamFirewallRule -DisplayName $name
}
Get-Process WebcamStream -ErrorAction SilentlyContinue | Stop-Process -Force
if ($Purge -and (Test-Path -LiteralPath $InstallDir)) {
    Remove-Item -LiteralPath $InstallDir -Recurse -Force
    Write-Host "Removed $InstallDir"
} else {
    Write-Host "Removed the service, scheduled task, and firewall rules."
    Write-Host "Left the files in $InstallDir. Pass -Purge to delete them, including the config."
}
Write-Host "Left the webcam allowed for every account. This uninstall does not turn it off."
