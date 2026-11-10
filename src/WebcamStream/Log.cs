namespace WebcamStream;

static class Log
{
    static readonly object Gate = new();
    static string? _path;

    public static bool UseConsole { get; set; } = true;

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
                if (info.Exists && info.Length > 1_000_000)
                {
                    var backup = _path + ".1";
                    File.Delete(backup);
                    File.Move(_path, backup);
                }
                File.AppendAllText(_path, line + Environment.NewLine);
            }
            catch
            {
                // Logging must not take down the stream.
            }
        }
    }
}
