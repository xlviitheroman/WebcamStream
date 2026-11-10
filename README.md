# WebcamStream

WebcamStream turns a Windows PC's webcam into an RTSP camera that a recorder can pull. ExacqVision is the recorder this was written for. Any recorder that can open a generic RTSP JPEG stream can use it the same way.

The picture is **1280×720 (720p) at 4 frames per second**. When the webcam can hand over MJPEG, those JPEG frames are packetized and sent. They are not decoded, not scaled, and not passed through a GPU. That is the whole point: a 4th-generation Intel machine can sit in a corner and act as a camera without a fan spinning up.

The program listens on **`0.0.0.0:554`**. 554 is the RTSP port. 544 is not. Exacq's address for the defaults is:

```text
rtsp://THIS-PC:554/webcam
```

An empty allow list means any computer that can reach that port may connect. The config can restrict it to IP addresses, subnets, hostnames, and FQDNs.

## Download

`release/WebcamStream-win-x64.zip` is the package to install. It contains:

- `WebcamStream.exe`, self-contained. Running it does not need a .NET install.
- `config.jsonc`
- `Install-WebcamStream.ps1` and `Uninstall-WebcamStream.ps1`

Unzip it, then use the install commands below.

SHA-256 of this build:

```text
b8f40138cc95103052d10f4eaff04d86607d5e41ada8c67fc5dd52e28ba3588c  WebcamStream.exe
0de398c5025bed46d1dedc410ff327413999b2eaa7fe2a1590ea680317cf799e  WebcamStream-win-x64.zip
```

The public repository is <https://github.com/xlviitheroman/WebcamStream>. Once the source is on that repository, `.github/workflows/release.yml` builds this same exe on a Windows runner and attaches it to release **v1.0.0**. Those downloads will be:

- <https://github.com/xlviitheroman/WebcamStream/releases/download/v1.0.0/WebcamStream.exe>
- <https://github.com/xlviitheroman/WebcamStream/releases/download/v1.0.0/WebcamStream-win-x64.zip>

## What you install, and where

Everything lives in **`C:\ProgramData\WebcamStream`**.

| File | What it is |
| --- | --- |
| `WebcamStream.exe` | The program. The release build includes the runtime, so this PC does not need .NET installed. |
| `config.jsonc` | Settings, with comments. Created on install, and also the first time the program runs if the file is missing. |
| `WebcamStream.log` | Log. Connections, the camera mode, and errors. Not every frame. |
| `status.json` | Camera mode, measured frame rate, and how many clients are connected. |

You can run it two ways:

- **Scheduled task (use this if someone stays signed in).** The task starts at logon, in that user's session. Windows only lets a normal desktop session open the webcam reliably, so this is the one that works on a PC that sits logged in at the console.
- **Windows service.** The service starts at boot as LocalSystem. It waits until someone signs in, then starts the camera in that person's session. Use this when the PC should come back after a reboot without a shortcut.

## .NET

The `.exe` attached to the GitHub release is **self-contained**. Do not install a .NET runtime to run it.

Windows 10 version 1607 or later, 64-bit, is required. Windows 7 and 8.1 cannot run this build.

To compile it yourself, install the **.NET 8 SDK (x64)** from <https://dotnet.microsoft.com/download/dotnet/8.0>.

If you publish a framework-dependent build (`--self-contained false`) instead of using the release exe, the PC that runs it needs the **.NET 8 Runtime (x64)**. On the download page that package is named **".NET Runtime"**. It is not the .NET Desktop Runtime, and it is not the ASP.NET Core Runtime.

```text
winget install --id Microsoft.DotNet.Runtime.8 -e --accept-package-agreements --accept-source-agreements
```

The installer checks for that runtime only when `WebcamStream.runtimeconfig.json` is sitting next to the exe. The release exe does not have that file.

## Install

Copy the release onto the PC, open an elevated PowerShell, and pick one mode.

```powershell
cd C:\path\to\the\release
.\Install-WebcamStream.ps1 -Mode ScheduledTask
```

or:

```powershell
.\Install-WebcamStream.ps1 -Mode Service
```

Add `-Start` to start it immediately. `-User "OFFICE\nvr"` chooses who the scheduled task runs as. The default is the account that runs the installer.

The script copies `WebcamStream.exe` to `C:\ProgramData\WebcamStream`, writes `config.jsonc` if it is not already there, and opens the firewall for TCP 554 and the UDP port range in the config. It does not overwrite a config you have already edited.

Uninstall:

```powershell
.\Uninstall-WebcamStream.ps1
.\Uninstall-WebcamStream.ps1 -Purge
```

`-Purge` also deletes `C:\ProgramData\WebcamStream`, including the config.

## Try it before installing

From the folder that contains `WebcamStream.exe`:

```text
WebcamStream.exe --list-cameras
WebcamStream.exe --console
```

`--list-cameras` prints every mode the webcam offers. You want a line that says `MJPG 1280x720`. That mode is the cheap one. YUY2 or NV12 at 720p still works, but this program then has to compress each frame itself, which costs more CPU.

`--console` runs in the current session until you press Ctrl+C. A generated picture, with no camera, is:

```text
WebcamStream.exe --console --test-pattern
```

## ExacqVision

1. In the client, open the server, then **Configure System**, then **Add IP Cameras**.
2. Click **New**.
3. Set **Device Type** to **RTSP**.
4. In **Hostname/IP Address**, enter the full URL:

   ```text
   rtsp://192.168.1.40:554/webcam
   ```

   Use this PC's address. The path `/webcam` and port `554` match the config defaults.
5. Leave the username and password empty unless you set them in `config.jsonc`.
6. Click **Apply**.

RTSP in Exacq carries video only. It does not carry motion metadata or a configuration channel. If Exacq offers a transport choice and UDP does not connect, use TCP. WebcamStream accepts both. TCP needs only port 554. UDP also uses the RTP port range, `50000`–`50015` with the defaults.

Put the Exacq server's address, subnet, or DNS name in `allow` before this PC is reachable from anywhere you do not trust. With the default empty list, the port is open to every interface.

## Config

`C:\ProgramData\WebcamStream\config.jsonc` is JSON with `//` comments. The copy the program writes already explains each setting. The allow list is re-read while the program is running. Port, path, camera, and picture size wait for a restart.

A locked-down example:

```jsonc
"allow": [
  "192.168.1.0/24",
  "10.8.0.0/255.255.255.0",
  "exacq.office.example.com",
  ".office.example.com"
]
```

| Entry | Who it allows |
| --- | --- |
| `10.1.1.20` | That address. |
| `192.168.1.0/24` | That subnet, CIDR form. |
| `10.0.0.0/255.255.0.0` | That subnet, mask form. |
| `exacq.office.example.com` | Whatever that hostname or FQDN resolves to. It is resolved again every `allowRefreshSeconds`. |
| `.office.example.com` | A client whose reverse DNS name ends in `office.example.com`. |

`listenAddress` stays `0.0.0.0` in the normal setup. That binds every IPv4 interface. `allow` decides who may connect. It does not change the bind address.

`fps` should stay at 3, 4, or 5. `width` and `height` should stay at 1280 and 720 unless `--list-cameras` shows the webcam has no 720p mode. The program will not scale the picture on the CPU. It picks the closest MJPEG mode and logs the real size.

`jpegQuality` matters only when the camera cannot deliver MJPEG. Passthrough ignores it.

`username` and `password` turn on RTSP Basic authentication. Leave both empty to not ask for a password. If you set them, put the same values in Exacq.

## Why it stays light

- The capture path asks Media Foundation for the camera's own MJPEG, and turns DXVA and the color converter off. No GPU.
- Frames the recorder will not see are dropped before any parse or copy that matters. The default is 4 per second, even if the camera is producing 30.
- The RTSP server sends JPEG over RTP (payload type 26). There is no ffmpeg process and no second transcoder.
- One process, a handful of threads, and a log line only when something connects or fails.

If `--list-cameras` shows no `MJPG` line, the fallback compresses NV12, YUY2, or RGB on the CPU at the configured frame rate. 720p at 4 fps is still a small job. It is not as small as passthrough.

## Camera privacy

Windows 10 and 11 will refuse the camera if desktop apps are not allowed to use it. The installer is already running as administrator, and it forces the webcam on for every account before it registers the service or the scheduled task.

It writes these values through the 64-bit registry API. `reg.exe` is not used, because it rejects key names that contain `\.` or `{...}` and reports that the key cannot be found. Firewall rules are added with `netsh`. `New-NetFirewallRule` is not used, because on some PCs that cmdlet stops with "Invalid class".

| Key | Value | What it does |
| --- | --- | --- |
| `HKLM\SOFTWARE\Policies\Microsoft\Windows\AppPrivacy` | `LetAppsAccessCamera` = `2` | Forces Windows apps to allow the camera. Any per-app deny list is removed. |
| `HKLM\SOFTWARE\Policies\Microsoft\Camera` | `AllowCamera` = `1` | Keeps the camera device enabled for the PC. |
| `...\CapabilityAccessManager\ConsentStore\webcam` | `Value` = `Allow` | Turns camera access on. |
| `...\ConsentStore\webcam\NonPackaged` | `Value` = `Allow` | Lets desktop apps, including WebcamStream, open the camera. |
| `...\DeviceAccess\Global\{E5323777-F976-4f5b-9B55-B94699C46E44}` | `Value` = `Allow` | Allows the webcam device interface. |
| Sensor permission for that same webcam id | `SensorPermissionState` = `1` | Allows the camera sensor. |

The machine keys cover the PC. The same consent values are then written for `.DEFAULT`, every signed-in user, every other profile under `ProfileList` whose `NTUSER.DAT` can be loaded, and the Default user template so the next new account is allowed too. A profile Windows has locked is skipped. The machine policy still forces the camera on for that account.

The installer also turns off Media Foundation frame-server mode. That broker returns `0x80070005` (access denied) to a Windows service even when the privacy switch is on. With it off, WebcamStream opens the camera device directly. LocalSystem, Local Service, and Network Service receive the same Allow values as a signed-in user. The program folder grants SYSTEM full control and Users read and execute, so the service and the signed-in worker can both start the exe.

The service itself stays in session 0. It starts a worker in the signed-in session, which is the session Windows will let open a camera. Someone who is already signed in may need to sign out once before Media Foundation notices the change. Uninstall removes the service, the task, and the firewall rules. It does not turn the camera back off and it does not turn frame-server mode back on.

The scheduled task and the service worker both run as the signed-in user, in that user's session. A service that tried to open the camera from session 0 usually gets access denied. WebcamStream does not do that.

## Build from source

```text
dotnet test
dotnet publish src/WebcamStream/WebcamStream.csproj -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -p:EnableCompressionInSingleFile=true -p:DebugType=none -o dist/win-x64
```

Copy `config/config.jsonc` next to the exe if you are assembling a folder by hand. The installer looks for it there and in `config\`. The exe also has the same text embedded, and writes it the first time it runs with no config file.

## Troubleshooting

| What you see | What to do |
| --- | --- |
| Installer stops with "Invalid class" or `reg.exe` cannot find a key | Use this copy of `Install-WebcamStream.ps1`. It opens the ports with `netsh` and writes the camera policy directly. Run it from an elevated PowerShell. |
| Exacq never connects | Confirm the URL, port 554, and the firewall rules named WebcamStream RTSP and WebcamStream RTP. The log shows rejected addresses when the allow list is the reason. |
| `0x80070005` or access denied | Run the installer again from an elevated PowerShell. It allows the camera for every account, including LocalSystem, and turns off the frame server that blocks a service. Sign out and back in, then start the service or the scheduled task. Close any other app that has the camera open. |
| No `MJPG` mode | The stream still works, using software JPEG. CPU use will be higher. 4 fps keeps it bounded. |
| Port already in use | Change `port` in the config. 554 is correct. Use another port only when some other program already owns it, and put that same port in the Exacq URL. |
| Two copies running | A mutex named `Global\WebcamStream` stops a second copy. Uninstall the service if you only want the scheduled task, or the other way around. The installer removes the other one when you install. |
