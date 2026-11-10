using System.Diagnostics;
using System.Runtime.InteropServices;
using System.ServiceProcess;

namespace WebcamStream;

sealed class WebcamStreamService : ServiceBase
{
    StreamSupervisor? _supervisor;

    public WebcamStreamService()
    {
        ServiceName = "WebcamStream";
        CanStop = true;
        AutoLog = false;
    }

    public static string? ConfigPath { get; set; }

    protected override void OnStart(string[] args)
    {
        try { RequestAdditionalTime(30000); } catch { }
        Log.ShareWithSignedInUsers(Path.Combine(AppConfig.ProgramDataDirectory, "status.json"));
        Log.Info("Windows service is running. The camera opens in the signed-in session.");
        _supervisor = new StreamSupervisor(ConfigPath);
        _supervisor.Start();
    }

    protected override void OnStop() => _supervisor?.Dispose();
}

sealed class StreamSupervisor : IDisposable
{
    readonly string? _configPath;
    readonly CancellationTokenSource _stop = new();
    readonly Thread _thread;
    IntPtr _job;
    IntPtr _process;

    public StreamSupervisor(string? configPath)
    {
        _configPath = configPath;
        _thread = new Thread(Loop) { IsBackground = true, Name = "webcam-supervisor" };
    }

    public void Start() => _thread.Start();

    void Loop()
    {
        if (OperatingSystem.IsWindows())
            _job = CreateKillOnCloseJob();
        while (!_stop.IsCancellationRequested)
        {
            try
            {
                if (!OperatingSystem.IsWindows())
                {
                    Log.Error("The Windows service supervisor only runs on Windows.");
                    return;
                }
                if (!StartWorker(out _process))
                {
                    if (_stop.Token.WaitHandle.WaitOne(5000)) break;
                    continue;
                }
                WaitForExit(_process, _stop.Token);
                CloseProcess(ref _process);
                if (_stop.IsCancellationRequested) break;
                Log.Warn("The camera worker exited. Starting it again in 3 seconds.");
                if (_stop.Token.WaitHandle.WaitOne(3000)) break;
            }
            catch (Exception ex)
            {
                Log.Error($"Supervisor: {ex.Message}");
                if (_stop.Token.WaitHandle.WaitOne(5000)) break;
            }
        }
        CloseProcess(ref _process);
    }

    bool StartWorker(out IntPtr process)
    {
        process = IntPtr.Zero;
        uint session = WTSGetActiveConsoleSessionId();
        if (session == 0xFFFFFFFF)
        {
            Log.Info("No one is logged on yet. The webcam opens after a user signs in.");
            return false;
        }
        if (!WTSQueryUserToken(session, out IntPtr token))
        {
            Log.Warn($"Could not take the signed-in user's token (session {session}, Win32 {Marshal.GetLastWin32Error()}). The service account is usually LocalSystem.");
            return false;
        }
        IntPtr primary = IntPtr.Zero;
        IntPtr env = IntPtr.Zero;
        try
        {
            // TOKEN_ALL_ACCESS. The duplicated token otherwise keeps session 0, and the
            // camera frame server then fails the worker with 0x80070005.
            if (!DuplicateTokenEx(token, 0x000F01FF, IntPtr.Zero, 2, 1, out primary)
                && !DuplicateTokenEx(token, 0x018B, IntPtr.Zero, 2, 1, out primary))
            {
                Log.Warn($"DuplicateTokenEx failed (Win32 {Marshal.GetLastWin32Error()}).");
                return false;
            }
            if (!SetTokenInformation(primary, 12, ref session, sizeof(uint)))
                Log.Warn($"Could not place the camera worker in session {session} (Win32 {Marshal.GetLastWin32Error()}).");
            CreateEnvironmentBlock(out env, primary, false);
            string exe = Environment.ProcessPath ?? Path.Combine(AppContext.BaseDirectory, "WebcamStream.exe");
            string config = string.IsNullOrWhiteSpace(_configPath)
                ? Path.Combine(AppConfig.ProgramDataDirectory, "config.jsonc")
                : _configPath;
            string command = $"\"{exe}\" --worker --config \"{config}\"";
            var desktop = Marshal.StringToHGlobalUni(@"winsta0\default");
            try
            {
                var si = new STARTUPINFO { cb = Marshal.SizeOf<STARTUPINFO>(), lpDesktop = desktop };
                if (!CreateProcessAsUser(primary, null, command, IntPtr.Zero, IntPtr.Zero, false,
                        0x08000000 | (env == IntPtr.Zero ? 0u : 0x00000400),
                        env, Path.GetDirectoryName(exe), ref si, out var pi))
                {
                    Log.Warn($"CreateProcessAsUser failed (Win32 {Marshal.GetLastWin32Error()}).");
                    return false;
                }
                process = pi.hProcess;
                if (_job != IntPtr.Zero)
                    AssignProcessToJobObject(_job, process);
                CloseHandle(pi.hThread);
                Log.Info($"Camera worker started in the signed-in session (pid {pi.dwProcessId}).");
                return true;
            }
            finally
            {
                Marshal.FreeHGlobal(desktop);
            }
        }
        finally
        {
            if (env != IntPtr.Zero) DestroyEnvironmentBlock(env);
            if (primary != IntPtr.Zero) CloseHandle(primary);
            CloseHandle(token);
        }
    }

    static void WaitForExit(IntPtr process, CancellationToken token)
    {
        while (!token.IsCancellationRequested)
        {
            if (WaitForSingleObject(process, 500) == 0)
                return;
        }
    }

    static void CloseProcess(ref IntPtr process)
    {
        if (process == IntPtr.Zero) return;
        TerminateProcess(process, 0);
        CloseHandle(process);
        process = IntPtr.Zero;
    }

    static IntPtr CreateKillOnCloseJob()
    {
        IntPtr job = CreateJobObject(IntPtr.Zero, null);
        if (job == IntPtr.Zero) return IntPtr.Zero;
        var info = new JOBOBJECT_EXTENDED_LIMIT_INFORMATION
        {
            BasicLimitInformation = new JOBOBJECT_BASIC_LIMIT_INFORMATION
            {
                LimitFlags = 0x00002000,
            },
        };
        int size = Marshal.SizeOf<JOBOBJECT_EXTENDED_LIMIT_INFORMATION>();
        if (!SetInformationJobObject(job, 9, ref info, (uint)size))
        {
            CloseHandle(job);
            return IntPtr.Zero;
        }
        return job;
    }

    public void Dispose()
    {
        _stop.Cancel();
        try { _thread.Join(4000); } catch { }
        CloseProcess(ref _process);
        if (_job != IntPtr.Zero) CloseHandle(_job);
        _stop.Dispose();
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    struct STARTUPINFO
    {
        public int cb;
        public IntPtr lpReserved;
        public IntPtr lpDesktop;
        public IntPtr lpTitle;
        public int dwX, dwY, dwXSize, dwYSize, dwXCountChars, dwYCountChars, dwFillAttribute, dwFlags;
        public short wShowWindow, cbReserved2;
        public IntPtr lpReserved2, hStdInput, hStdOutput, hStdError;
    }

    [StructLayout(LayoutKind.Sequential)]
    struct PROCESS_INFORMATION
    {
        public IntPtr hProcess, hThread;
        public int dwProcessId, dwThreadId;
    }

    [StructLayout(LayoutKind.Sequential)]
    struct JOBOBJECT_BASIC_LIMIT_INFORMATION
    {
        public long PerProcessUserTimeLimit;
        public long PerJobUserTimeLimit;
        public uint LimitFlags;
        public uint Padding1;
        public nuint MinimumWorkingSetSize;
        public nuint MaximumWorkingSetSize;
        public uint ActiveProcessLimit;
        public uint Padding2;
        public nuint Affinity;
        public uint PriorityClass;
        public uint SchedulingClass;
    }

    [StructLayout(LayoutKind.Sequential)]
    struct IO_COUNTERS
    {
        public ulong ReadOperationCount, WriteOperationCount, OtherOperationCount;
        public ulong ReadTransferCount, WriteTransferCount, OtherTransferCount;
    }

    [StructLayout(LayoutKind.Sequential)]
    struct JOBOBJECT_EXTENDED_LIMIT_INFORMATION
    {
        public JOBOBJECT_BASIC_LIMIT_INFORMATION BasicLimitInformation;
        public IO_COUNTERS IoInfo;
        public nuint ProcessMemoryLimit;
        public nuint JobMemoryLimit;
        public nuint PeakProcessMemoryUsed;
        public nuint PeakJobMemoryUsed;
    }

    [DllImport("kernel32.dll")] static extern uint WTSGetActiveConsoleSessionId();
    [DllImport("wtsapi32.dll", SetLastError = true)] static extern bool WTSQueryUserToken(uint sessionId, out IntPtr token);
    [DllImport("advapi32.dll", SetLastError = true)] static extern bool DuplicateTokenEx(IntPtr token, uint access, IntPtr attributes, int level, int type, out IntPtr primary);
    [DllImport("advapi32.dll", SetLastError = true)] static extern bool SetTokenInformation(IntPtr token, int informationClass, ref uint sessionId, int length);
    [DllImport("userenv.dll", SetLastError = true)] static extern bool CreateEnvironmentBlock(out IntPtr env, IntPtr token, bool inherit);
    [DllImport("userenv.dll", SetLastError = true)] static extern bool DestroyEnvironmentBlock(IntPtr env);
    [DllImport("advapi32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    static extern bool CreateProcessAsUser(IntPtr token, string? app, string command, IntPtr processAttributes, IntPtr threadAttributes, bool inherit, uint flags, IntPtr env, string? directory, ref STARTUPINFO si, out PROCESS_INFORMATION pi);
    [DllImport("kernel32.dll", SetLastError = true)] static extern IntPtr CreateJobObject(IntPtr attributes, string? name);
    [DllImport("kernel32.dll", SetLastError = true)] static extern bool SetInformationJobObject(IntPtr job, int infoClass, ref JOBOBJECT_EXTENDED_LIMIT_INFORMATION info, uint length);
    [DllImport("kernel32.dll", SetLastError = true)] static extern bool AssignProcessToJobObject(IntPtr job, IntPtr process);
    [DllImport("kernel32.dll", SetLastError = true)] static extern uint WaitForSingleObject(IntPtr handle, uint milliseconds);
    [DllImport("kernel32.dll", SetLastError = true)] static extern bool TerminateProcess(IntPtr process, uint code);
    [DllImport("kernel32.dll", SetLastError = true)] static extern bool CloseHandle(IntPtr handle);
}
