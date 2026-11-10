#Requires -RunAsAdministrator
<#
.SYNOPSIS
  Install WebcamStream into C:\ProgramData\WebcamStream as a scheduled task or a Windows service.

.DESCRIPTION
  The scheduled task is the one to use when someone is logged on at this PC. It runs in that
  person's session, which is what Windows requires to open the webcam.

  The service starts at boot. It waits until someone signs in, then opens the camera in that
  session. The service itself runs as LocalSystem.

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

foreach ($name in @("WebcamStream-RTSP-TCP", "WebcamStream-RTP-UDP")) {
    $existing = Get-NetFirewallRule -Name $name -ErrorAction SilentlyContinue
    if ($existing) { Remove-NetFirewallRule -Name $name }
}
New-NetFirewallRule -Name "WebcamStream-RTSP-TCP" -DisplayName "WebcamStream RTSP" -Direction Inbound -Action Allow -Protocol TCP -LocalPort $port -Profile Any | Out-Null
New-NetFirewallRule -Name "WebcamStream-RTP-UDP" -DisplayName "WebcamStream RTP" -Direction Inbound -Action Allow -Protocol UDP -LocalPort "$rtpStart-$rtpEnd" -Profile Any | Out-Null

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
