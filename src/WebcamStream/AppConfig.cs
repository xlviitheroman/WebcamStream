using System.Text.Json;
using System.Text.Json.Serialization;

namespace WebcamStream;

sealed class AppConfig
{
    public string ListenAddress { get; set; } = "0.0.0.0";
    public int Port { get; set; } = 554;
    public string Path { get; set; } = "/webcam";
    public int Fps { get; set; } = 4;
    public int Width { get; set; } = 1280;
    public int Height { get; set; } = 720;
    public int JpegQuality { get; set; } = 40;
    public string CameraName { get; set; } = "";
    public int CameraIndex { get; set; } = -1;
    public string[] Allow { get; set; } = Array.Empty<string>();
    public int AllowRefreshSeconds { get; set; } = 60;
    public int MaxClients { get; set; } = 4;
    public string Username { get; set; } = "";
    public string Password { get; set; } = "";
    public int RtpPortStart { get; set; } = 50000;
    public int RtpPortCount { get; set; } = 8;
    public string LogPath { get; set; } = "";
    public bool WriteStatus { get; set; } = true;
    public bool TestPattern { get; set; }

    [JsonIgnore]
    public string ConfigFile { get; set; } = "";

    [JsonIgnore]
    public string Directory { get; set; } = "";

    public static string ProgramDataDirectory =>
        System.IO.Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
            "WebcamStream");

    public static AppConfig Load(string? explicitPath)
    {
        var file = ResolvePath(explicitPath, create: true);
        return Read(file);
    }

    public static AppConfig Read(string file)
    {
        var json = File.ReadAllText(file);
        var options = SerializerOptions();
        var config = JsonSerializer.Deserialize<AppConfig>(json, options) ?? new AppConfig();
        config.ConfigFile = file;
        config.Directory = System.IO.Path.GetDirectoryName(file) ?? "";
        config.Normalize();
        return config;
    }

    public void Normalize()
    {
        if (string.IsNullOrWhiteSpace(ListenAddress))
            ListenAddress = "0.0.0.0";
        if (Port is < 0 or > 65535)
            Port = 554;
        if (string.IsNullOrWhiteSpace(Path))
            Path = "/webcam";
        if (!Path.StartsWith('/'))
            Path = "/" + Path;
        if (Path.Length > 1)
            Path = Path.TrimEnd('/');
        Fps = Math.Clamp(Fps, 1, 15);
        if (Width < 16) Width = 1280;
        if (Height < 16) Height = 720;
        Width -= Width % 16;
        Height -= Height % 16;
        JpegQuality = Math.Clamp(JpegQuality, 1, 95);
        AllowRefreshSeconds = Math.Clamp(AllowRefreshSeconds, 10, 3600);
        MaxClients = Math.Clamp(MaxClients, 1, 16);
        if (RtpPortStart < 1) RtpPortStart = 50000;
        RtpPortCount = Math.Clamp(RtpPortCount, 1, 64);
        Allow ??= Array.Empty<string>();
        CameraName ??= "";
        Username ??= "";
        Password ??= "";
        LogPath ??= "";
        if (string.IsNullOrWhiteSpace(LogPath) && !string.IsNullOrEmpty(Directory))
            LogPath = System.IO.Path.Combine(Directory, "WebcamStream.log");
    }

    public string StatusPath =>
        string.IsNullOrEmpty(Directory)
            ? "status.json"
            : System.IO.Path.Combine(Directory, "status.json");

    static string ResolvePath(string? explicitPath, bool create)
    {
        if (!string.IsNullOrWhiteSpace(explicitPath))
        {
            if (!File.Exists(explicitPath))
            {
                if (!create)
                    throw new FileNotFoundException("Config file was not found.", explicitPath);
                WriteDefault(explicitPath);
            }
            return explicitPath;
        }

        var programData = System.IO.Path.Combine(ProgramDataDirectory, "config.jsonc");
        if (File.Exists(programData))
            return programData;

        var beside = BesideExe("config.jsonc") ?? BesideExe("config.json");
        if (beside != null)
            return beside;

        if (create)
        {
            try
            {
                WriteDefault(programData);
                return programData;
            }
            catch (Exception ex) when (ex is UnauthorizedAccessException or IOException)
            {
                var fallback = System.IO.Path.Combine(AppContext.BaseDirectory, "config.jsonc");
                WriteDefault(fallback);
                return fallback;
            }
        }

        throw new FileNotFoundException(
            "No config file was found. Expected " + programData);
    }

    public static void WriteDefault(string path)
    {
        var dir = System.IO.Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(dir))
            System.IO.Directory.CreateDirectory(dir);
        var bytes = DefaultConfigBytes();
        File.WriteAllBytes(path, bytes);
    }

    public static byte[] DefaultConfigBytes()
    {
        var stream = typeof(AppConfig).Assembly.GetManifestResourceStream("WebcamStream.DefaultConfig.jsonc");
        if (stream == null)
            throw new InvalidOperationException("The default config resource is missing from the program.");
        using (stream)
        {
            using var mem = new MemoryStream();
            stream.CopyTo(mem);
            return mem.ToArray();
        }
    }

    static string? BesideExe(string name)
    {
        var path = System.IO.Path.Combine(AppContext.BaseDirectory, name);
        return File.Exists(path) ? path : null;
    }

    public static JsonSerializerOptions SerializerOptions() => new()
    {
        PropertyNameCaseInsensitive = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
        WriteIndented = true,
    };
}
