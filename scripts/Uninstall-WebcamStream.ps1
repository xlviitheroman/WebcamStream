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

$script:UninstallLogPath = Join-Path $PSScriptRoot "Uninstall-WebcamStream.log"
$script:UninstallTranscript = $false

function Write-UninstallNote {
    param([string]$Message)
    $line = "{0:yyyy-MM-dd HH:mm:ss} {1}" -f (Get-Date), $Message
    try { Add-Content -LiteralPath $script:UninstallLogPath -Value $line -Encoding UTF8 } catch { }
}

# PowerShell 7 and 32-bit Windows PowerShell talk to the wrong service database.
# Continue in the 64-bit Windows PowerShell 5.1 host.
if (-not ([Environment]::Is64BitProcess -and $PSVersionTable.PSEdition -eq "Desktop")) {
    $hostExe = Join-Path $env:windir "System32\WindowsPowerShell\v1.0\powershell.exe"
    if (-not [Environment]::Is64BitProcess) {
        $native = Join-Path $env:windir "Sysnative\WindowsPowerShell\v1.0\powershell.exe"
        if (Test-Path -LiteralPath $native) { $hostExe = $native }
    }
    if (Test-Path -LiteralPath $hostExe) {
        $forward = @("-NoProfile", "-ExecutionPolicy", "Bypass", "-File", $PSCommandPath)
        foreach ($entry in $PSBoundParameters.GetEnumerator()) {
            if ($entry.Value -is [switch]) {
                if ($entry.Value) { $forward += "-" + $entry.Key }
            } else {
                $forward += "-" + $entry.Key
                $forward += [string]$entry.Value
            }
        }
        Write-UninstallNote "This host is $($PSVersionTable.PSEdition) $($PSVersionTable.PSVersion) $([IntPtr]::Size * 8)-bit. Relaunching $hostExe."
        & $hostExe @forward
        Write-UninstallNote "Relaunched uninstaller exited with code $LASTEXITCODE."
        exit $LASTEXITCODE
    }
}

try {
    Start-Transcript -Path $script:UninstallLogPath -Append -Force -IncludeInvocationHeader | Out-Null
    $script:UninstallTranscript = $true
} catch {
    Write-UninstallNote "Could not start a transcript: $($_.Exception.Message)"
}

function Complete-UninstallLog {
    Write-Host "Uninstall log: $script:UninstallLogPath"
    $copy = Join-Path $InstallDir "Uninstall.log"
    if ($script:UninstallTranscript) {
        try { Stop-Transcript | Out-Null } catch { }
        $script:UninstallTranscript = $false
    }
    if (-not (Test-Path -LiteralPath $script:UninstallLogPath)) { return }
    if ($Purge) { return }
    try {
        if (-not (Test-Path -LiteralPath $InstallDir)) { return }
        Copy-Item -LiteralPath $script:UninstallLogPath -Destination $copy -Force
    } catch { }
}

function Remove-WebcamFirewallRule {
    param([string]$DisplayName, [string]$Program)
    $netsh = Join-Path $env:windir "System32\netsh.exe"
    if (-not [Environment]::Is64BitProcess) {
        $native = Join-Path $env:windir "Sysnative\netsh.exe"
        if (Test-Path -LiteralPath $native) { $netsh = $native }
    }
    $arguments = "advfirewall firewall delete rule name=`"$DisplayName`""
    if ($Program) { $arguments = "advfirewall firewall delete rule name=all program=`"$Program`"" }
    $psi = New-Object System.Diagnostics.ProcessStartInfo
    $psi.FileName = $netsh
    $psi.Arguments = $arguments
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
    $sc = Join-Path $env:windir "System32\sc.exe"
    if (-not [Environment]::Is64BitProcess) {
        $native = Join-Path $env:windir "Sysnative\sc.exe"
        if (Test-Path -LiteralPath $native) { $sc = $native }
    }
    $psi = New-Object System.Diagnostics.ProcessStartInfo
    $psi.FileName = $sc
    $psi.Arguments = $Arguments
    $psi.UseShellExecute = $false
    $psi.RedirectStandardOutput = $true
    $psi.RedirectStandardError = $true
    $psi.CreateNoWindow = $true
    $p = [System.Diagnostics.Process]::Start($psi)
    $out = $p.StandardOutput.ReadToEnd() + $p.StandardError.ReadToEnd()
    $p.WaitForExit()
    Write-Host "sc.exe $Arguments"
    Write-Host "sc.exe exited $($p.ExitCode): $($out.Trim())"
    return [pscustomobject]@{ ExitCode = $p.ExitCode; Output = $out }
}

function Test-WebcamServiceGone {
    param($Result)
    return ($Result.ExitCode -ne 0 -and $Result.Output -match "1060")
}

function Stop-WebcamStreamProcess {
    Get-Process -Name WebcamStream -ErrorAction SilentlyContinue | Stop-Process -Force -ErrorAction SilentlyContinue
}

function Remove-WebcamService {
    $existing = Invoke-Sc "query WebcamStream"
    if (Test-WebcamServiceGone $existing) { return }

    # The service is set to restart itself. Turn that off before killing the process,
    # or Windows starts a new copy and the delete never sticks.
    Invoke-Sc "failureflag WebcamStream 0" | Out-Null
    Invoke-Sc "failure WebcamStream reset= 0 actions= `"`"" | Out-Null
    Invoke-Sc "config WebcamStream start= disabled" | Out-Null
    Invoke-Sc "stop WebcamStream" | Out-Null

    $deadline = (Get-Date).AddSeconds(20)
    do {
        Start-Sleep -Milliseconds 400
        $query = Invoke-Sc "query WebcamStream"
        if (Test-WebcamServiceGone $query) { return }
        # STATE 1 is STOPPED. The word is localized; the number is not.
        if ($query.Output -match "STATE\s+:\s+1\b") { break }
    } while ((Get-Date) -lt $deadline)

    # A camera call that Windows never answers ignores the stop control.
    # The service stays installed until that process is gone.
    Stop-WebcamStreamProcess
    Start-Sleep -Seconds 1

    for ($try = 0; $try -lt 8; $try++) {
        $deleted = Invoke-Sc "delete WebcamStream"
        if ($deleted.ExitCode -eq 0 -or (Test-WebcamServiceGone $deleted)) { break }
        Stop-WebcamStreamProcess
        Start-Sleep -Milliseconds 700
    }

    $goneBy = (Get-Date).AddSeconds(5)
    do {
        $still = Invoke-Sc "query WebcamStream"
        if (Test-WebcamServiceGone $still) { return }
        Start-Sleep -Milliseconds 400
    } while ((Get-Date) -lt $goneBy)

    # DeleteService was accepted but a handle is still open, usually services.msc
    # or the service process. Drop the registration itself once the process is dead.
    Stop-WebcamStreamProcess
    Start-Sleep -Milliseconds 500
    $alive = Get-Process -Name WebcamStream -ErrorAction SilentlyContinue
    if (-not $alive) {
        foreach ($set in @("CurrentControlSet", "ControlSet001", "ControlSet002")) {
            $key = "HKLM:\SYSTEM\$set\Services\WebcamStream"
            if (Test-Path -LiteralPath $key) {
                Remove-Item -LiteralPath $key -Recurse -Force -ErrorAction SilentlyContinue
            }
        }
    }

    $left = Invoke-Sc "query WebcamStream"
    if (-not (Test-WebcamServiceGone $left)) {
        throw "The WebcamStream service is still registered. Close services.msc if it is open, then run Uninstall-WebcamStream.ps1 again."
    }
}

function Repair-DeniedCameraPolicy {
    # Installers through 1.0.4 wrote LetAppsAccessCamera = 2. That value is Force Deny.
    $view = [Microsoft.Win32.RegistryView]::Registry64
    $hklm = [Microsoft.Win32.RegistryKey]::OpenBaseKey([Microsoft.Win32.RegistryHive]::LocalMachine, $view)
    try {
        $key = $hklm.OpenSubKey("SOFTWARE\Policies\Microsoft\Windows\AppPrivacy", $true)
        if ($null -eq $key) { return }
        try {
            $current = $key.GetValue("LetAppsAccessCamera")
            if ($current -eq 2) {
                $key.SetValue("LetAppsAccessCamera", 1, [Microsoft.Win32.RegistryValueKind]::DWord)
                Write-Host "Corrected the camera policy from Force Deny to Force Allow."
            }
        } finally { $key.Dispose() }
    } finally { $hklm.Dispose() }
}

try {
Write-Host "WebcamStream uninstaller"
Write-Host ("Time: {0:yyyy-MM-dd HH:mm:ss zzz}" -f (Get-Date))
Write-Host ("Windows: {0}" -f [Environment]::OSVersion.VersionString)
Write-Host ("PowerShell: {0} {1} {2}-bit" -f $PSVersionTable.PSEdition, $PSVersionTable.PSVersion, ([IntPtr]::Size * 8))
Write-Host ("User: {0}\{1}  Purge: {2}" -f $env:USERDOMAIN, $env:USERNAME, [bool]$Purge)
Write-Host "Install directory: $InstallDir"

Remove-WebcamService
Repair-DeniedCameraPolicy
$task = Get-ScheduledTask -TaskName "WebcamStream" -ErrorAction SilentlyContinue
if ($task) { Unregister-ScheduledTask -TaskName "WebcamStream" -Confirm:$false }
foreach ($name in @("WebcamStream", "WebcamStream RTSP", "WebcamStream RTP", "WebcamStream-RTSP-TCP", "WebcamStream-RTP-UDP")) {
    Remove-WebcamFirewallRule -DisplayName $name
}
Remove-WebcamFirewallRule -Program (Join-Path $InstallDir "WebcamStream.exe")
Stop-WebcamStreamProcess
if ($Purge -and (Test-Path -LiteralPath $InstallDir)) {
    Remove-Item -LiteralPath $InstallDir -Recurse -Force
    Write-Host "Removed $InstallDir"
} else {
    Write-Host "Removed the service, scheduled task, and firewall rules."
    Write-Host "Left the files in $InstallDir. Pass -Purge to delete them, including the config."
}
Write-Host "Left the webcam allowed for every account. This uninstall does not turn it off."
} catch {
    Write-Host ("UNINSTALL FAILED:`n" + ($_ | Out-String))
    exit 1
} finally {
    Complete-UninstallLog
}
