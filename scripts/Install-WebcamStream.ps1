#Requires -RunAsAdministrator
<#
.SYNOPSIS
  Install WebcamStream into C:\ProgramData\WebcamStream as a scheduled task or a Windows service.

.DESCRIPTION
  The scheduled task is the one to use when someone is logged on at this PC. It runs in that
  person's session, which is what Windows requires to open the webcam.

  The service starts at boot. It waits until someone signs in, then opens the camera in that
  session. The service itself runs as LocalSystem.

  Because this script is running elevated, it also forces the webcam on for every account.
  Windows apps are forced to Allow, and desktop-app camera consent is set to Allow in each
  profile that can be opened. Uninstall does not turn the camera back off.

.EXAMPLE
  .\Install-WebcamStream.ps1 -Mode ScheduledTask
  .\Install-WebcamStream.ps1 -Mode Service
  .\Install-WebcamStream.ps1 -Mode ScheduledTask -User "OFFICE\nvr" -Start
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)]
    [ValidateSet("Service", "ScheduledTask")]
    [string]$Mode,

    [string]$SourceDir,
    [string]$InstallDir = $(Join-Path $env:ProgramData "WebcamStream"),
    [string]$User,
    [switch]$Start
)

$ErrorActionPreference = "Stop"
if (Get-Variable -Name PSNativeCommandUseErrorActionPreference -ErrorAction SilentlyContinue) {
    $PSNativeCommandUseErrorActionPreference = $false
}

# PowerShell 7 and 32-bit Windows PowerShell cannot see the real firewall provider
# or the 64-bit policy keys. Continue in the 64-bit Windows PowerShell 5.1 host.
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
        & $hostExe @forward
        exit $LASTEXITCODE
    }
}

function Find-Source {
    param([string]$Hint)
    $candidates = @()
    if ($Hint) { $candidates += $Hint }
    $candidates += (Join-Path $PSScriptRoot "..\dist\win-x64")
    $candidates += $PSScriptRoot
    $candidates += (Join-Path $PSScriptRoot "..")
    foreach ($dir in $candidates) {
        if (-not $dir) { continue }
        $exe = Join-Path $dir "WebcamStream.exe"
        if (Test-Path -LiteralPath $exe) { return (Resolve-Path -LiteralPath $dir).Path }
    }
    throw "WebcamStream.exe was not found. Pass -SourceDir, or run this from a copy of the release folder."
}

function Read-StreamSettings {
    param([string]$Path)
    $port = 554
    $rtpStart = 50000
    $rtpCount = 8
    if (-not (Test-Path -LiteralPath $Path)) { return $port, $rtpStart, ($rtpStart + $rtpCount * 2 - 1) }
    $text = Get-Content -LiteralPath $Path -Raw
    $m = [regex]::Match($text, '"port"\s*:\s*(\d+)')
    if ($m.Success) { $port = [int]$m.Groups[1].Value }
    $m = [regex]::Match($text, '"rtpPortStart"\s*:\s*(\d+)')
    if ($m.Success) { $rtpStart = [int]$m.Groups[1].Value }
    $m = [regex]::Match($text, '"rtpPortCount"\s*:\s*(\d+)')
    if ($m.Success) { $rtpCount = [int]$m.Groups[1].Value }
    if (($rtpStart % 2) -eq 1) { $rtpStart++ }
    $rtpEnd = $rtpStart + ($rtpCount * 2) - 1
    return $port, $rtpStart, $rtpEnd
}

function Test-FrameworkBuild {
    param([string]$Dir)
    $runtimeConfig = Join-Path $Dir "WebcamStream.runtimeconfig.json"
    if (-not (Test-Path -LiteralPath $runtimeConfig)) { return }
    $list = & dotnet --list-runtimes 2>$null
    if ($list -match "Microsoft\.NETCore\.App 8\.") { return }
    throw @"
This copy of WebcamStream is framework-dependent, and the .NET 8 Runtime is not installed.

Install the .NET 8 Runtime (x64). The download page calls it ".NET Runtime".
Do not install the .NET Desktop Runtime for this, and do not install the ASP.NET Core Runtime.

  winget install --id Microsoft.DotNet.Runtime.8 -e --accept-package-agreements --accept-source-agreements

Or download the x64 installer from https://dotnet.microsoft.com/download/dotnet/8.0
under ".NET Runtime" (not SDK, not Desktop, not ASP.NET).

The release .exe published with this project is self-contained and does not need that install.
"@
}

function Invoke-Netsh {
    param([string]$Arguments)
    $netsh = Join-Path $env:windir "System32\netsh.exe"
    if (-not [Environment]::Is64BitProcess) {
        $native = Join-Path $env:windir "Sysnative\netsh.exe"
        if (Test-Path -LiteralPath $native) { $netsh = $native }
    }
    $psi = New-Object System.Diagnostics.ProcessStartInfo
    $psi.FileName = $netsh
    $psi.Arguments = $Arguments
    $psi.UseShellExecute = $false
    $psi.RedirectStandardOutput = $true
    $psi.RedirectStandardError = $true
    $psi.CreateNoWindow = $true
    $p = [System.Diagnostics.Process]::Start($psi)
    $out = $p.StandardOutput.ReadToEnd() + $p.StandardError.ReadToEnd()
    $p.WaitForExit()
    return @{ Code = $p.ExitCode; Output = $out.Trim() }
}

function Set-WebcamFirewall {
    param([string]$DisplayName, [string]$Protocol, [string]$Ports)
    $firewall = Get-Service -Name MpsSvc -ErrorAction SilentlyContinue
    if ($firewall -and $firewall.Status -ne "Running") {
        if ($firewall.StartType -eq "Disabled") { Set-Service -Name MpsSvc -StartupType Manual }
        Start-Service -Name MpsSvc
    }
    # New-NetFirewallRule uses a WMI class that is missing on some PCs ("Invalid class").
    # netsh talks to the firewall service directly.
    Invoke-Netsh "advfirewall firewall delete rule name=`"$DisplayName`"" | Out-Null
    $added = Invoke-Netsh "advfirewall firewall add rule name=`"$DisplayName`" dir=in action=allow protocol=$Protocol localport=$Ports profile=any enable=yes"
    if ($added.Code -ne 0) {
        throw "Could not open inbound $Protocol $Ports in Windows Firewall.`n$($added.Output)"
    }
}

function Enable-WebcamForAllUsers {
    Write-Host "Forcing the webcam on for every account on this PC."
    if (-not ("WebcamStream.HiveLoader" -as [type])) {
        Add-Type -TypeDefinition @'
using System;
using System.Runtime.InteropServices;
namespace WebcamStream {
    public static class HiveLoader {
        const uint TokenAdjustPrivileges = 0x0020;
        const uint TokenQuery = 0x0008;
        const uint PrivilegeEnabled = 0x2;
        static readonly IntPtr HkeyUsers = new IntPtr(unchecked((int)0x80000003));

        [DllImport("kernel32.dll")] static extern IntPtr GetCurrentProcess();
        [DllImport("kernel32.dll", SetLastError=true)] static extern bool CloseHandle(IntPtr handle);
        [DllImport("advapi32.dll", SetLastError=true)]
        static extern bool OpenProcessToken(IntPtr process, uint access, out IntPtr token);
        [DllImport("advapi32.dll", SetLastError=true, CharSet=CharSet.Unicode)]
        static extern bool LookupPrivilegeValue(string system, string name, out Luid luid);
        [DllImport("advapi32.dll", SetLastError=true)]
        static extern bool AdjustTokenPrivileges(IntPtr token, bool disableAll, ref TokenPrivileges state, int length, IntPtr previous, IntPtr returned);
        [DllImport("advapi32.dll", CharSet=CharSet.Unicode)]
        static extern int RegLoadKey(IntPtr hive, string subKey, string file);
        [DllImport("advapi32.dll", CharSet=CharSet.Unicode)]
        static extern int RegUnLoadKey(IntPtr hive, string subKey);

        [StructLayout(LayoutKind.Sequential)] struct Luid { public uint Low; public int High; }
        [StructLayout(LayoutKind.Sequential)] struct LuidAndAttributes { public Luid Luid; public uint Attributes; }
        [StructLayout(LayoutKind.Sequential)] struct TokenPrivileges { public uint Count; public LuidAndAttributes Privilege; }

        public static void Enable(string name) {
            IntPtr token;
            if (!OpenProcessToken(GetCurrentProcess(), TokenAdjustPrivileges | TokenQuery, out token))
                throw new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error());
            try {
                Luid luid;
                if (!LookupPrivilegeValue(null, name, out luid))
                    throw new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error());
                LuidAndAttributes privilege = new LuidAndAttributes();
                privilege.Luid = luid;
                privilege.Attributes = PrivilegeEnabled;
                TokenPrivileges privileges = new TokenPrivileges();
                privileges.Count = 1;
                privileges.Privilege = privilege;
                if (!AdjustTokenPrivileges(token, false, ref privileges, 0, IntPtr.Zero, IntPtr.Zero))
                    throw new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error());
                int error = Marshal.GetLastWin32Error();
                if (error != 0) throw new System.ComponentModel.Win32Exception(error);
            } finally { CloseHandle(token); }
        }

        public static int Load(string subKey, string file) { return RegLoadKey(HkeyUsers, subKey, file); }
        public static int Unload(string subKey) { return RegUnLoadKey(HkeyUsers, subKey); }
    }
}
'@
    }

    $view = [Microsoft.Win32.RegistryView]::Registry64
    $hklm = [Microsoft.Win32.RegistryKey]::OpenBaseKey([Microsoft.Win32.RegistryHive]::LocalMachine, $view)
    $hku = [Microsoft.Win32.RegistryKey]::OpenBaseKey([Microsoft.Win32.RegistryHive]::Users, $view)
    $webcamGuid = "{E5323777-F976-4f5b-9B55-B94699C46E44}"
    $consent = @(
        "SOFTWARE\Microsoft\Windows\CurrentVersion\CapabilityAccessManager\ConsentStore\webcam",
        "SOFTWARE\Microsoft\Windows\CurrentVersion\CapabilityAccessManager\ConsentStore\webcam\NonPackaged",
        ("SOFTWARE\Microsoft\Windows\CurrentVersion\DeviceAccess\Global\" + $webcamGuid)
    )

    function Set-RegString([Microsoft.Win32.RegistryKey]$Root, [string]$SubKey, [string]$Name, [string]$Data) {
        $key = $Root.CreateSubKey($SubKey)
        if ($null -eq $key) { throw "Could not create registry key $SubKey." }
        try { $key.SetValue($Name, $Data, [Microsoft.Win32.RegistryValueKind]::String) }
        finally { $key.Dispose() }
    }
    function Set-RegDword([Microsoft.Win32.RegistryKey]$Root, [string]$SubKey, [string]$Name, [int]$Data) {
        $key = $Root.CreateSubKey($SubKey)
        if ($null -eq $key) { throw "Could not create registry key $SubKey." }
        try { $key.SetValue($Name, $Data, [Microsoft.Win32.RegistryValueKind]::DWord) }
        finally { $key.Dispose() }
    }
    function Set-WebcamConsent([Microsoft.Win32.RegistryKey]$Root, [bool]$Machine) {
        foreach ($sub in $consent) { Set-RegString $Root $sub "Value" "Allow" }
        if ($Machine) {
            Set-RegDword $Root ("SOFTWARE\Microsoft\Windows NT\CurrentVersion\Sensor\Overrides\" + $webcamGuid) "SensorPermissionState" 1
        } else {
            Set-RegDword $Root ("SOFTWARE\Microsoft\Windows NT\CurrentVersion\Sensor\Permissions\" + $webcamGuid) "SensorPermissionState" 1
        }
    }

    # 2 is Force Allow. 0 leaves the choice to each user, and 1 forces the camera off.
    Set-RegDword $hklm "SOFTWARE\Policies\Microsoft\Windows\AppPrivacy" "LetAppsAccessCamera" 2
    $privacy = $hklm.CreateSubKey("SOFTWARE\Policies\Microsoft\Windows\AppPrivacy")
    try {
        foreach ($exception in @("LetAppsAccessCamera_ForceDenyTheseApps", "LetAppsAccessCamera_UserInControlOfTheseApps")) {
            $privacy.DeleteValue($exception, $false)
        }
    } finally { $privacy.Dispose() }
    # 1 allows the camera device. 0 disables it for the whole PC.
    Set-RegDword $hklm "SOFTWARE\Policies\Microsoft\Camera" "AllowCamera" 1
    Set-WebcamConsent $hklm $true

    function Test-UserAccount([string]$Name) {
        if ($Name -like "*_Classes") { return $false }
        if ($Name -eq ".DEFAULT") { return $true }
        if ($Name -match '^(S-1-5-18|S-1-5-19|S-1-5-20)$') { return $false }
        return $Name -match '^S-1-\d+(-\d+)+$'
    }

    $loaded = @{}
    foreach ($name in @($hku.GetSubKeyNames())) { $loaded[$name] = $true }
    foreach ($name in @($loaded.Keys)) {
        if (-not (Test-UserAccount $name)) { continue }
        $hive = $hku.OpenSubKey($name, $true)
        if ($null -eq $hive) { continue }
        try { Set-WebcamConsent $hive $false }
        catch { Write-Host "Could not update camera consent for $name. The machine policy still forces the camera on. $($_.Exception.Message)" }
        finally { $hive.Dispose() }
    }

    $canLoadOffline = $true
    try {
        [WebcamStream.HiveLoader]::Enable("SeBackupPrivilege")
        [WebcamStream.HiveLoader]::Enable("SeRestorePrivilege")
    } catch {
        $canLoadOffline = $false
        Write-Host "Could not enable backup privileges, so signed-out profiles were not edited. The machine policy still forces the camera on."
    }

    $mounted = New-Object System.Collections.Generic.List[string]
    function Mount-OfflineHive([string]$Name, [string]$File) {
        if (-not $canLoadOffline -or -not (Test-Path -LiteralPath $File)) { return $false }
        $code = [WebcamStream.HiveLoader]::Load($Name, $File)
        if ($code -eq 0 -or $code -eq 183) {
            $mounted.Add($Name)
            return $true
        }
        Write-Host "Could not open $File ($((New-Object System.ComponentModel.Win32Exception($code)).Message)). The machine policy still forces the camera on."
        return $false
    }
    function Update-MountedHive([string]$Name) {
        $hive = $hku.OpenSubKey($Name, $true)
        if ($null -eq $hive) { throw "Loaded profile $Name was not visible in the registry." }
        try { Set-WebcamConsent $hive $false }
        finally { $hive.Dispose() }
    }

    try {
        $profileList = $hklm.OpenSubKey("SOFTWARE\Microsoft\Windows NT\CurrentVersion\ProfileList")
        if ($null -ne $profileList) {
            try {
                foreach ($sid in @($profileList.GetSubKeyNames())) {
                    if (-not (Test-UserAccount $sid)) { continue }
                    if ($loaded.ContainsKey($sid)) { continue }
                    $profile = $profileList.OpenSubKey($sid)
                    if ($null -eq $profile) { continue }
                    try { $image = [string]$profile.GetValue("ProfileImagePath") }
                    finally { $profile.Dispose() }
                    if (-not $image) { continue }
                    $ntuser = Join-Path ([Environment]::ExpandEnvironmentVariables($image)) "NTUSER.DAT"
                    $mount = "WebcamStream_" + $sid.Replace("-", "_")
                    if (-not (Mount-OfflineHive $mount $ntuser)) { continue }
                    try { Update-MountedHive $mount }
                    catch { Write-Host "Could not update camera consent for $sid. The machine policy still forces the camera on. $($_.Exception.Message)" }
                }
            } finally { $profileList.Dispose() }
        }
        $defaultProfile = Join-Path $env:SystemDrive "Users\Default\NTUSER.DAT"
        if (Mount-OfflineHive "WebcamStream_DefaultUser" $defaultProfile) {
            try { Update-MountedHive "WebcamStream_DefaultUser" }
            catch { Write-Host "Could not update the Default user profile. The machine policy still forces the camera on. $($_.Exception.Message)" }
        }
    } finally {
        foreach ($name in @($mounted)) {
            [gc]::Collect()
            [gc]::WaitForPendingFinalizers()
            $code = [WebcamStream.HiveLoader]::Unload($name)
            if ($code -ne 0) {
                Write-Host "The profile hive $name is still loaded ($((New-Object System.ComponentModel.Win32Exception($code)).Message))."
            }
        }
        $hku.Dispose()
        $hklm.Dispose()
    }

    Write-Host "Webcam access is forced on for Windows apps, and desktop apps are allowed."
    Write-Host "A person who is already signed in may need to sign out once before the camera opens."
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
    if ($p.ExitCode -ne 0 -and $out -notmatch "1060") {
        throw "sc.exe $Arguments failed ($($p.ExitCode)): $out"
    }
}

$source = Find-Source $SourceDir
Test-FrameworkBuild $source
New-Item -ItemType Directory -Force -Path $InstallDir | Out-Null
Copy-Item -LiteralPath (Join-Path $source "WebcamStream.exe") -Destination (Join-Path $InstallDir "WebcamStream.exe") -Force
Get-ChildItem -LiteralPath $source -Filter "*.pdb" -ErrorAction SilentlyContinue | ForEach-Object {
    Copy-Item -LiteralPath $_.FullName -Destination (Join-Path $InstallDir $_.Name) -Force
}

$config = Join-Path $InstallDir "config.jsonc"
if (-not (Test-Path -LiteralPath $config)) {
    $template = Join-Path $source "config.jsonc"
    if (-not (Test-Path -LiteralPath $template)) { $template = Join-Path $PSScriptRoot "..\config\config.jsonc" }
    if (Test-Path -LiteralPath $template) {
        Copy-Item -LiteralPath $template -Destination $config
    } else {
        Write-Host "No config template was next to the exe. The program will write the default config the first time it starts."
    }
}

$exe = Join-Path $InstallDir "WebcamStream.exe"
$port, $rtpStart, $rtpEnd = Read-StreamSettings $config

Set-WebcamFirewall -DisplayName "WebcamStream RTSP" -Protocol "TCP" -Ports "$port"
Set-WebcamFirewall -DisplayName "WebcamStream RTP" -Protocol "UDP" -Ports "$rtpStart-$rtpEnd"

Enable-WebcamForAllUsers

if ($Mode -eq "Service") {
    Write-Host "Installing the WebcamStream Windows service."
    Write-Host "It runs as LocalSystem, and opens the webcam after someone signs in to this PC."
    Invoke-Sc "stop WebcamStream"
    Start-Sleep -Seconds 1
    Invoke-Sc "delete WebcamStream"
    Start-Sleep -Seconds 1
    $task = Get-ScheduledTask -TaskName "WebcamStream" -ErrorAction SilentlyContinue
    if ($task) { Unregister-ScheduledTask -TaskName "WebcamStream" -Confirm:$false }
    $bin = '"' + $exe + '" --service --config "' + $config + '"'
    $quoted = $bin.Replace('"', '\"')
    Invoke-Sc "create WebcamStream binPath= `"$quoted`" start= auto obj= LocalSystem DisplayName= `"WebcamStream`""
    Invoke-Sc "description WebcamStream `"Webcam RTSP stream for recorders such as ExacqVision. Low CPU, no GPU.`""
    Invoke-Sc "failure WebcamStream reset= 86400 actions= restart/5000/restart/15000/restart/60000"
    Invoke-Sc "config WebcamStream start= auto"
    if ($Start) { Invoke-Sc "start WebcamStream" }
    Write-Host "Service installed. Config: $config"
    Write-Host "RTSP URL: rtsp://<this-pc>:$port/webcam"
    return
}

if (-not $User) {
    if ($env:USERDOMAIN -and $env:USERDOMAIN -ne $env:COMPUTERNAME) { $User = "$env:USERDOMAIN\$env:USERNAME" }
    else { $User = $env:USERNAME }
}
Write-Host "Installing a scheduled task that runs at logon as $User."
Write-Host "This is the reliable way to open the webcam, because it runs in the signed-on session."
Invoke-Sc "stop WebcamStream"
Start-Sleep -Seconds 1
Invoke-Sc "delete WebcamStream"

$action = New-ScheduledTaskAction -Execute $exe -Argument "--console --config `"$config`"" -WorkingDirectory $InstallDir
$trigger = New-ScheduledTaskTrigger -AtLogOn -User $User
$settings = New-ScheduledTaskSettingsSet -AllowStartIfOnBatteries -DontStopIfGoingOnBatteries -StartWhenAvailable -RestartCount 10 -RestartInterval (New-TimeSpan -Minutes 1) -MultipleInstances IgnoreNew -ExecutionTimeLimit ([TimeSpan]::Zero)
$existingTask = Get-ScheduledTask -TaskName "WebcamStream" -ErrorAction SilentlyContinue
if ($existingTask) { Unregister-ScheduledTask -TaskName "WebcamStream" -Confirm:$false }
Register-ScheduledTask -TaskName "WebcamStream" -Action $action -Trigger $trigger -Settings $settings -User $User -RunLevel Limited -Description "Webcam RTSP stream for recorders such as ExacqVision." | Out-Null
if ($Start) {
    Start-Process -FilePath $exe -ArgumentList "--console", "--config", $config -WorkingDirectory $InstallDir
}
Write-Host "Scheduled task installed. It starts the next time $User signs in."
Write-Host "Config: $config"
Write-Host "RTSP URL: rtsp://<this-pc>:$port/webcam"
