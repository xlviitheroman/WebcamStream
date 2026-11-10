using System.Security.AccessControl;
using System.Security.Principal;

namespace WebcamStream;

static class Log
{
    static readonly object Gate = new();
    static string? _path;
    static bool _shared;

    internal static int MaxBytes = 1_000_000;
    internal static int KeepFiles = 3;

    public static bool UseConsole { get; set; } = true;

    public static void ShareWithSignedInUsers(string path)
    {
        if (!OperatingSystem.IsWindows() || string.IsNullOrWhiteSpace(path))
            return;
        try
        {
            var info = new FileInfo(path);
            if (!info.Exists)
            {
                var dir = info.DirectoryName;
                if (!string.IsNullOrEmpty(dir))
                    Directory.CreateDirectory(dir);
                File.WriteAllBytes(path, Array.Empty<byte>());
                info.Refresh();
            }
            var security = info.GetAccessControl();
            var users = new SecurityIdentifier(WellKnownSidType.BuiltinUsersSid, null);
            security.AddAccessRule(new FileSystemAccessRule(
                users,
                FileSystemRights.Modify,
                InheritanceFlags.None,
                PropagationFlags.None,
                AccessControlType.Allow));
            info.SetAccessControl(security);
        }
        catch
        {
            // The service creates the log and can grant this. The worker only needs to append.
        }
    }

    public static void SetPath(string? path)
    {
        lock (Gate)
        {
            _path = string.IsNullOrWhiteSpace(path) ? null : path;
            if (_path == null)
                return;
            var dir = Path.GetDirectoryName(_path);
            if (!string.IsNullOrEmpty(dir))
                Directory.CreateDirectory(dir);
        }
    }

    public static void Info(string message) => Write("info", message);
    public static void Warn(string message) => Write("warn", message);
    public static void Error(string message) => Write("error", message);

    static void Write(string level, string message)
    {
        var line = $"{DateTime.Now:yyyy-MM-dd HH:mm:ss} {level,-5} {message}";
        lock (Gate)
        {
            if (UseConsole)
            {
                try
                {
                    Console.WriteLine(line);
                    Console.Out.Flush();
                }
                catch
                {
                    UseConsole = false;
                }
            }
            if (_path == null)
                return;
            try
            {
                var info = new FileInfo(_path);
                if (info.Exists && info.Length >= MaxBytes && TryRotate(_path))
                {
                    _shared = false;
                    string rolled = $"{DateTime.Now:yyyy-MM-dd HH:mm:ss} info  Log rolled over. Older lines are in {Path.GetFileName(_path)}.1";
                    File.AppendAllText(_path, rolled + Environment.NewLine);
                }
                File.AppendAllText(_path, line + Environment.NewLine);
                if (!_shared)
                {
                    ShareWithSignedInUsers(_path);
                    _shared = true;
                }
            }
            catch
            {
                // Logging must not take down the stream.
            }
        }
    }

    static bool TryRotate(string path)
    {
        int keep = Math.Clamp(KeepFiles, 1, 8);
        try
        {
            for (int i = keep; i >= 1; i--)
            {
                string src = i == 1 ? path : path + "." + (i - 1);
                string dst = path + "." + i;
                if (!File.Exists(src))
                    continue;
                if (File.Exists(dst))
                    File.Delete(dst);
                File.Move(src, dst);
            }
            return !File.Exists(path);
        }
        catch
        {
            return false;
        }
    }
}
