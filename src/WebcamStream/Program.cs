using System.Net;
using System.Net.Sockets;
using System.Runtime.CompilerServices;
using System.Text.Json;

[assembly: InternalsVisibleTo("WebcamStream.Tests")]

namespace WebcamStream;

static class Program
{
    static int Main(string[] args)
    {
        if (Has(args, "--help") || Has(args, "-h") || Has(args, "/?"))
        {
            Console.WriteLine(Help());
            return 0;
        }
        if (Has(args, "--version"))
        {
            Console.WriteLine(VersionLine());
            return 0;
        }

        try
        {
            if (Has(args, "--list-cameras"))
                return ListCameras();
            if (Has(args, "--service"))
                return RunService(args);
            if (Has(args, "--console") || Has(args, "--worker") || Has(args, "--test-pattern"))
                return RunForeground(args);
            Console.Error.WriteLine(Help());
            return 1;
        }
        catch (Exception ex)
        {
            Log.Error(ex.Message);
            return 1;
        }
    }

    static int RunService(string[] args)
    {
        if (!OperatingSystem.IsWindows())
        {
            Console.Error.WriteLine("The Windows service only runs on Windows. Use --console --test-pattern here.");
            return 1;
        }
        WebcamStreamService.ConfigPath = ArgValue(args, "--config");
        System.ServiceProcess.ServiceBase.Run(new WebcamStreamService());
        return 0;
    }

    static int ListCameras()
    {
        if (!OperatingSystem.IsWindows())
        {
            Console.Error.WriteLine("Listing cameras uses Media Foundation and only works on Windows.");
            return 1;
        }
        var cameras = MediaFoundationCapture.List();
        if (cameras.Count == 0)
        {
            Console.WriteLine("No webcam was found.");
            return 1;
        }
        foreach (var camera in cameras)
            Console.WriteLine(MediaFoundationCapture.Describe(camera));
        return 0;
    }

    static int RunForeground(string[] args)
    {
        using var mutex = new Mutex(false, OperatingSystem.IsWindows() ? @"Global\WebcamStream" : "WebcamStream");
        if (!mutex.WaitOne(0))
        {
            Log.Error("WebcamStream is already running.");
            return 1;
        }

        var config = AppConfig.Load(ArgValue(args, "--config"));
        bool testPattern = config.TestPattern || Has(args, "--test-pattern");
        if (!testPattern && !OperatingSystem.IsWindows())
        {
            Log.Error("Opening a webcam only works on Windows. Add --test-pattern to stream a generated picture.");
            return 1;
        }
        Log.SetPath(config.LogPath);
        Log.Info($"{VersionLine()}");
        Log.Info($"Config {config.ConfigFile}");
        if (config.Fps > 8)
            Log.Warn($"fps is {config.Fps}. 3 to 5 keeps CPU use low.");

        using var access = new ReloadableAccess(config);
        using var server = new RtspServer(config, access.Access);
        server.Start();
        Log.Info($"Listening on rtsp://{config.ListenAddress}:{server.Port}{config.Path}");
        foreach (var ip in LocalAddresses())
            Log.Info($"ExacqVision address: rtsp://{ip}:{server.Port}{config.Path}");
        Log.Info(access.Access.AllowAll
            ? "Allow list is empty, so any computer that can reach this port may connect."
            : "Allow list is on. Other computers are refused.");

        using var stop = new CancellationTokenSource();
        Console.CancelKeyPress += (_, e) => { e.Cancel = true; stop.Cancel(); };
        var state = new CaptureState { Mode = testPattern ? "test-pattern" : "starting" };
        var capture = new Thread(() =>
        {
            try
            {
                if (testPattern)
                    RunPattern(config, server, stop.Token, state);
                else
                    MediaFoundationCapture.Capture(config, (jpeg, label) =>
                    {
                        var frame = RtpJpeg.Packetize(jpeg, Interlocked.Increment(ref state.FrameId), label);
                        state.Mode = label;
                        server.Publish(frame);
                    }, stop.Token);
            }
            catch (Exception ex) when (!stop.IsCancellationRequested)
            {
                Log.Error(ex.Message);
                stop.Cancel();
            }
        })
        { IsBackground = true, Name = "capture" };
        capture.Start();

        var started = DateTime.UtcNow;
        int lastId = 0;
        var lastCountAt = started;
        double measured = 0;
        while (!stop.IsCancellationRequested)
        {
            Thread.Sleep(1000);
            if (!config.WriteStatus) continue;
            int id = Volatile.Read(ref state.FrameId);
            var now = DateTime.UtcNow;
            double seconds = (now - lastCountAt).TotalSeconds;
            if (seconds >= 5)
            {
                measured = (id - lastId) / seconds;
                lastId = id;
                lastCountAt = now;
            }
            TryWriteStatus(config, server, state.Mode, measured);
        }
        capture.Join(2000);
        Log.Info("Stopped.");
        return 0;
    }

    static void RunPattern(AppConfig config, RtspServer server, CancellationToken token, CaptureState state)
    {
        int tick = 0;
        state.Mode = $"test-pattern {config.Width}x{config.Height}";
        while (!token.IsCancellationRequested)
        {
            var jpeg = Pixels.Pattern(config.Width, config.Height, config.JpegQuality, tick++);
            var frame = RtpJpeg.Packetize(jpeg, Interlocked.Increment(ref state.FrameId), state.Mode);
            server.Publish(frame);
            int delay = Math.Max(1, 1000 / Math.Clamp(config.Fps, 1, 15));
            if (token.WaitHandle.WaitOne(delay)) break;
        }
    }

    sealed class CaptureState
    {
        public int FrameId;
        public string Mode = "";
    }

    static void TryWriteStatus(AppConfig config, RtspServer server, string mode, double measuredFps)
    {
        try
        {
            var frame = server.Latest;
            var status = new
            {
                camera = mode,
                width = frame?.Width ?? config.Width,
                height = frame?.Height ?? config.Height,
                configuredFps = config.Fps,
                measuredFps = Math.Round(measuredFps, 2),
                clients = server.ClientCount,
                listen = $"{config.ListenAddress}:{server.Port}",
                path = config.Path,
                gpu = false,
            };
            var json = JsonSerializer.Serialize(status, new JsonSerializerOptions { WriteIndented = true });
            var path = config.StatusPath;
            var dir = Path.GetDirectoryName(path);
            if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
            File.WriteAllText(path, json);
        }
        catch
        {
        }
    }

    static IEnumerable<IPAddress> LocalAddresses()
    {
        foreach (var ni in System.Net.NetworkInformation.NetworkInterface.GetAllNetworkInterfaces())
        {
            if (ni.OperationalStatus != System.Net.NetworkInformation.OperationalStatus.Up) continue;
            if (ni.NetworkInterfaceType == System.Net.NetworkInformation.NetworkInterfaceType.Loopback) continue;
            foreach (var addr in ni.GetIPProperties().UnicastAddresses)
            {
                if (addr.Address.AddressFamily == AddressFamily.InterNetwork)
                    yield return addr.Address;
            }
        }
    }

    static string VersionLine()
    {
#if SELF_CONTAINED
        return "WebcamStream 1.0.0 self-contained (this .exe does not need a .NET runtime installed)";
#else
        return "WebcamStream 1.0.0 framework-dependent (needs the .NET 8 Runtime, x64)";
#endif
    }

    static string Help() =>
$@"{VersionLine()}

Turns this PC's webcam into an RTSP camera for a recorder such as ExacqVision.
The stream is 720p at 4 frames per second. MJPEG from the camera is forwarded
as-is, so the GPU is not used and a 4th-generation Intel CPU stays quiet.

Config file, created on first run if it is missing:
  {Path.Combine(AppConfig.ProgramDataDirectory, "config.jsonc")}
Comments in that file explain every setting. The RTSP port is 554.
544 is not the RTSP port.

Run in this session, for a test:
  WebcamStream.exe --console --config ""C:\ProgramData\WebcamStream\config.jsonc""
  WebcamStream.exe --list-cameras
  WebcamStream.exe --console --test-pattern

Install so it comes back at logon or at boot. From an elevated PowerShell:
  .\Install-WebcamStream.ps1 -Mode ScheduledTask
  .\Install-WebcamStream.ps1 -Mode Service

The scheduled task runs in the signed-on user's session, which is the reliable
way to open a webcam on Windows 10 and 11. The service starts at boot and
launches the camera after someone signs in.

ExacqVision: Configure System, Add IP Cameras, New, Device Type RTSP,
Hostname/IP Address:
  rtsp://THIS-PC:554/webcam

.NET
  The release .exe is self-contained. Do not install a runtime to run it.
  To build from source, install the .NET 8 SDK (x64).
  A framework-dependent build needs the .NET 8 Runtime (x64), the package
  named "".NET Runtime"", not the Desktop Runtime and not the ASP.NET Core
  Runtime. winget id: Microsoft.DotNet.Runtime.8
";

    static bool Has(string[] args, string name) =>
        args.Any(a => string.Equals(a, name, StringComparison.OrdinalIgnoreCase));

    static string? ArgValue(string[] args, string name)
    {
        for (int i = 0; i < args.Length; i++)
        {
            if (!string.Equals(args[i], name, StringComparison.OrdinalIgnoreCase)) continue;
            if (i + 1 < args.Length) return args[i + 1];
        }
        return null;
    }

    sealed class ReloadableAccess : IDisposable
    {
        FileSystemWatcher? _watcher;
        public AccessControl Access { get; }

        public ReloadableAccess(AppConfig config)
        {
            Access = new AccessControl();
            Access.Replace(config.Allow);
            if (string.IsNullOrEmpty(config.ConfigFile) || !File.Exists(config.ConfigFile))
                return;
            var dir = Path.GetDirectoryName(config.ConfigFile);
            var name = Path.GetFileName(config.ConfigFile);
            if (string.IsNullOrEmpty(dir)) return;
            _watcher = new FileSystemWatcher(dir, name) { NotifyFilter = NotifyFilters.LastWrite | NotifyFilters.Size };
            _watcher.Changed += (_, _) => Reload(config);
            _watcher.EnableRaisingEvents = true;
        }

        void Reload(AppConfig config)
        {
            try
            {
                Thread.Sleep(200);
                var fresh = AppConfig.Read(config.ConfigFile);
                config.Allow = fresh.Allow;
                config.Username = fresh.Username;
                config.Password = fresh.Password;
                config.Fps = fresh.Fps;
                Access.Replace(fresh.Allow);
                Log.Info("Reloaded the allow list from the config file.");
                if (fresh.Port != config.Port || fresh.Width != config.Width || fresh.Height != config.Height
                    || !string.Equals(fresh.Path, config.Path, StringComparison.OrdinalIgnoreCase)
                    || !string.Equals(fresh.CameraName, config.CameraName, StringComparison.Ordinal))
                    Log.Info("Port, path, camera, or picture size changed. Restart the service or task to apply that.");
            }
            catch (Exception ex)
            {
                Log.Warn($"Config reload failed, keeping the previous allow list: {ex.Message}");
            }
        }

        public void Dispose() => _watcher?.Dispose();
    }
}
