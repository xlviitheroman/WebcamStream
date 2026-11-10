using System.Runtime.InteropServices;

namespace WebcamStream;

sealed record CameraMode(string Subtype, int Width, int Height, double Fps);

sealed record CameraDevice(int Index, string Name, IReadOnlyList<CameraMode> Modes);

static class MediaFoundationCapture
{
    const int FirstVideo = unchecked((int)0xFFFFFFFC);
    const int AllStreams = unchecked((int)0xFFFFFFFE);
    const int NoMoreTypes = unchecked((int)0xC00D36B9);
    const int MfVersion = 0x00020070;
    const int MfStartupNoSocket = 1;

    static readonly Guid SourceType = new("c60ac5fe-252a-478f-a0ef-bc8fa5f7cad3");
    static readonly Guid Vidcap = new("8ac3587a-4ae7-42d8-99e0-0a6013eef90f");
    static readonly Guid FriendlyName = new("60d0e559-52f8-4fa2-bbce-acdb34a8ec01");
    static readonly Guid MajorType = new("48eba18e-f8c9-4687-bf11-0a74c9f96a8f");
    static readonly Guid Subtype = new("f7e34c9a-42e8-4714-b74b-cb29d72c35e5");
    static readonly Guid FrameSize = new("1652c33d-d6b2-4012-b834-72030849a37d");
    static readonly Guid FrameRate = new("c459a2e8-3d2c-4e44-b132-fee5156c7bb0");
    static readonly Guid DefaultStride = new("644b4e48-1e02-4516-b0eb-c01ca9d49ac6");
    static readonly Guid Video = new("73646976-0000-0010-8000-00aa00389b71");
    static readonly Guid Mjpg = new("47504A4D-0000-0010-8000-00AA00389B71");
    static readonly Guid Yuy2 = new("32595559-0000-0010-8000-00AA00389B71");
    static readonly Guid Nv12 = new("3231564E-0000-0010-8000-00AA00389B71");
    static readonly Guid Rgb32 = new("00000016-0000-0010-8000-00AA00389B71");
    static readonly Guid DisableConverters = new("98d5b065-1374-4847-8d5d-31520fee7156");
    static readonly Guid DisableDxva = new("aa456cfd-3943-4a1e-a77d-1838c0ea2e35");
    static readonly Guid MediaSourceId = new("279a808d-aec7-40c8-9c6b-a6b492c78a66");

    static readonly object StartupGate = new();
    static bool _started;

    public static IReadOnlyList<CameraDevice> List()
    {
        EnsureStarted();
        var opened = Enumerate();
        try { return opened.Select(c => c.ToInfo()).ToList(); }
        finally
        {
            foreach (var camera in opened) camera.Release();
        }
    }

    public static void Capture(AppConfig config, Action<EncodedJpeg, string> onFrame, CancellationToken token)
    {
        EnsureStarted();
        var devices = Enumerate();
        try
        {
            var chosen = Choose(devices, config);
            if (chosen == null)
                throw new InvalidOperationException("No webcam was found. Plug in a camera and try --list-cameras.");
            Run(chosen, config, onFrame, token);
        }
        finally
        {
            foreach (var device in devices)
                device.Release();
        }
    }

    static void Run(OpenCamera camera, AppConfig config, Action<EncodedJpeg, string> onFrame, CancellationToken token)
    {
        var mode = PickMode(camera.Modes, config);
        if (mode == null)
            throw new InvalidOperationException($"Camera '{camera.Name}' has no usable 8-bit video mode.");

        Check(camera.Reader.SetStreamSelection(AllStreams, false), "SetStreamSelection");
        Check(camera.Reader.SetStreamSelection(FirstVideo, true), "SetStreamSelection");
        Check(camera.Reader.SetCurrentMediaType(FirstVideo, IntPtr.Zero, mode.Type), "SetCurrentMediaType");

        string kind = SubtypeName(mode.Subtype);
        bool passthrough = mode.Subtype == Mjpg;
        Log.Info($"Camera '{camera.Name}' {kind} {mode.Width}x{mode.Height} @ {mode.Fps:0.##} fps, sending {config.Fps} fps. " +
                 (passthrough ? "MJPEG passthrough, no decode, no GPU." : "Software JPEG, no GPU."));

        var interval = TimeSpan.FromSeconds(1.0 / Math.Clamp(config.Fps, 1, 15));
        var next = DateTime.UtcNow;
        bool warnedHuffman = false;
        while (!token.IsCancellationRequested)
        {
            int hr = camera.Reader.ReadSample(FirstVideo, 0, out _, out int flags, out _, out IntPtr samplePtr);
            if (hr < 0)
                throw new InvalidOperationException(AccessDenied(hr, "ReadSample"));
            if (samplePtr == IntPtr.Zero)
            {
                if ((flags & 0x1) != 0)
                    throw new InvalidOperationException("The camera stopped delivering frames.");
                continue;
            }
            IMFSample? sample = null;
            IMFMediaBuffer? buffer = null;
            try
            {
                if (DateTime.UtcNow < next)
                    continue;
                next = DateTime.UtcNow + interval;
                sample = (IMFSample)Marshal.GetObjectForIUnknown(samplePtr);
                Check(sample.ConvertToContiguousBuffer(out buffer), "ConvertToContiguousBuffer");
                Check(buffer.Lock(out IntPtr data, out _, out int length), "Lock");
                try
                {
                    var bytes = new byte[length];
                    Marshal.Copy(data, bytes, 0, length);
                    EncodedJpeg? jpeg;
                    string label;
                    if (passthrough)
                    {
                        jpeg = JpegCodec.TryParse(bytes);
                        label = "mjpeg-passthrough";
                        if (jpeg == null || !jpeg.Supported)
                        {
                            Log.Warn("The camera JPEG is not a baseline 4:2:0 or 4:2:2 frame this stream can carry. Pick another mode with --list-cameras.");
                            continue;
                        }
                        if (!jpeg.StandardHuffman && !warnedHuffman)
                        {
                            warnedHuffman = true;
                            Log.Warn("This camera uses non-standard Huffman tables. Some recorders, including some RTSP JPEG decoders, will not show a picture. A camera mode that uses standard tables is the low-CPU fix.");
                        }
                    }
                    else if (mode.Subtype == Nv12)
                    {
                        int stride = Stride(mode.Type, mode.Width);
                        jpeg = Pixels.EncodeNv12(bytes, mode.Width, mode.Height, Math.Abs(stride), Math.Abs(stride), config.JpegQuality);
                        label = "nv12-jpeg";
                    }
                    else if (mode.Subtype == Yuy2)
                    {
                        int stride = Stride(mode.Type, mode.Width * 2);
                        jpeg = Pixels.EncodeYuy2(bytes, mode.Width, mode.Height, Math.Abs(stride), config.JpegQuality);
                        label = "yuy2-jpeg";
                    }
                    else
                    {
                        int stride = Stride(mode.Type, mode.Width * 4);
                        jpeg = Pixels.EncodeBgra(bytes, mode.Width, mode.Height, stride, config.JpegQuality);
                        label = "rgb-jpeg";
                    }
                    if (jpeg != null && jpeg.Supported && jpeg.Scan.Length > 0)
                        onFrame(jpeg, $"{label} {jpeg.Width}x{jpeg.Height}");
                }
                finally
                {
                    buffer.Unlock();
                }
            }
            finally
            {
                if (buffer != null) Marshal.ReleaseComObject(buffer);
                if (sample != null) Marshal.ReleaseComObject(sample);
                Marshal.Release(samplePtr);
            }
        }
    }

    static int Stride(IMFMediaType type, int fallback)
    {
        int hr = type.GetUINT32(DefaultStride, out int stride);
        if (hr < 0 || stride == 0) return fallback;
        return stride;
    }

    static OpenMode? PickMode(IReadOnlyList<OpenMode> modes, AppConfig config)
    {
        OpenMode? best = null;
        double bestScore = double.NegativeInfinity;
        foreach (var mode in modes)
        {
            if (mode.Width < 16 || mode.Height < 16 || mode.Width > 2040 || mode.Height > 2040)
                continue;
            if ((mode.Width & 7) != 0 || (mode.Height & 7) != 0)
                continue;
            string name = SubtypeName(mode.Subtype);
            if (name == "other") continue;
            double score = 0;
            if (mode.Subtype == Mjpg) score += 1_000_000;
            else if (mode.Subtype == Nv12) score += 2_000;
            else if (mode.Subtype == Yuy2) score += 1_000;
            else score += 100;
            long area = (long)mode.Width * mode.Height;
            long target = (long)config.Width * config.Height;
            if (mode.Width == config.Width && mode.Height == config.Height) score += 80_000;
            score -= Math.Abs(area - target) / 400.0;
            if (mode.Subtype != Mjpg && area > target * 2) score -= 50_000;
            double fps = mode.Fps <= 0 ? 30 : mode.Fps;
            score -= Math.Abs(fps - config.Fps) * 20;
            if (fps > config.Fps && fps <= config.Fps * 3) score += 30;
            if (score > bestScore)
            {
                bestScore = score;
                best = mode;
            }
        }
        return best;
    }

    static OpenCamera? Choose(List<OpenCamera> cameras, AppConfig config)
    {
        if (cameras.Count == 0) return null;
        if (!string.IsNullOrWhiteSpace(config.CameraName))
        {
            var match = cameras.FirstOrDefault(c => c.Name.Contains(config.CameraName, StringComparison.OrdinalIgnoreCase));
            if (match == null)
                throw new InvalidOperationException($"No camera name contains '{config.CameraName}'. Run --list-cameras.");
            return match;
        }
        int index = config.CameraIndex < 0 ? 0 : config.CameraIndex;
        if (index >= cameras.Count)
            throw new InvalidOperationException($"Camera index {index} is out of range. {cameras.Count} camera(s) are connected.");
        return cameras[index];
    }

    static List<OpenCamera> Enumerate()
    {
        Check(MFCreateAttributes(out var attrs, 2), "MFCreateAttributes");
        try
        {
            Check(attrs.SetGUID(SourceType, Vidcap), "SetGUID");
            Check(MFEnumDeviceSources(attrs, out IntPtr array, out int count), "MFEnumDeviceSources");
            var list = new List<OpenCamera>();
            try
            {
                for (int i = 0; i < count; i++)
                {
                    IntPtr punk = Marshal.ReadIntPtr(array, i * IntPtr.Size);
                    var activate = (IMFActivate)Marshal.GetObjectForIUnknown(punk);
                    Marshal.Release(punk);
                    string name = ReadName(activate, i);
                    list.Add(Open(activate, i, name));
                }
            }
            finally
            {
                Marshal.FreeCoTaskMem(array);
            }
            return list;
        }
        finally
        {
            Marshal.ReleaseComObject(attrs);
        }
    }

    static OpenCamera Open(IMFActivate activate, int index, string name)
    {
        Check(activate.ActivateObject(MediaSourceId, out IntPtr sourcePtr), "ActivateObject");
        var source = (IMFMediaSource)Marshal.GetObjectForIUnknown(sourcePtr);
        Marshal.Release(sourcePtr);
        Check(MFCreateAttributes(out var readerAttrs, 3), "MFCreateAttributes");
        try
        {
            readerAttrs.SetUINT32(DisableConverters, 1);
            readerAttrs.SetUINT32(DisableDxva, 1);
            Check(MFCreateSourceReaderFromMediaSource(source, readerAttrs, out var reader), "MFCreateSourceReaderFromMediaSource");
            var modes = new List<OpenMode>();
            for (int t = 0; t < 128; t++)
            {
                int hr = reader.GetNativeMediaType(FirstVideo, t, out var mediaType);
                if (hr == NoMoreTypes) break;
                if (hr < 0) break;
                try
                {
                    if (mediaType.GetGUID(MajorType, out var major) < 0 || major != Video) continue;
                    if (mediaType.GetGUID(Subtype, out var sub) < 0) continue;
                    if (mediaType.GetUINT64(FrameSize, out long size) < 0) continue;
                    uint w = (uint)(size >> 32);
                    uint h = (uint)size;
                    double fps = 0;
                    if (mediaType.GetUINT64(FrameRate, out long rate) >= 0)
                    {
                        uint num = (uint)(rate >> 32);
                        uint den = (uint)rate;
                        if (den != 0) fps = num / (double)den;
                    }
                    modes.Add(new OpenMode(sub, (int)w, (int)h, fps, mediaType));
                    mediaType = null!;
                }
                finally
                {
                    if (mediaType != null) Marshal.ReleaseComObject(mediaType);
                }
            }
            return new OpenCamera(index, name, activate, source, reader, modes);
        }
        finally
        {
            Marshal.ReleaseComObject(readerAttrs);
        }
    }

    static string ReadName(IMFActivate activate, int index)
    {
        if (activate.GetAllocatedString(FriendlyName, out IntPtr str, out int len) < 0 || str == IntPtr.Zero)
            return $"Camera {index}";
        try { return Marshal.PtrToStringUni(str, len) ?? $"Camera {index}"; }
        finally { Marshal.FreeCoTaskMem(str); }
    }

    public static string Describe(CameraDevice device)
    {
        var lines = new List<string> { $"[{device.Index}] {device.Name}" };
        if (device.Modes.Count == 0) lines.Add("    (no video modes reported)");
        foreach (var mode in device.Modes.OrderByDescending(m => m.Subtype == "MJPG").ThenBy(m => Math.Abs(m.Width * m.Height - 1280 * 720)))
            lines.Add($"    {mode.Subtype,-5} {mode.Width}x{mode.Height} @ {mode.Fps:0.##} fps");
        return string.Join(Environment.NewLine, lines);
    }

    static string SubtypeName(Guid subtype)
    {
        if (subtype == Mjpg) return "MJPG";
        if (subtype == Yuy2) return "YUY2";
        if (subtype == Nv12) return "NV12";
        if (subtype == Rgb32) return "RGB32";
        return "other";
    }

    static void EnsureStarted()
    {
        lock (StartupGate)
        {
            if (_started) return;
            CoInitializeEx(IntPtr.Zero, 0);
            int hr = MFStartup(MfVersion, MfStartupNoSocket);
            if (hr < 0) hr = MFStartup(0x00010070, MfStartupNoSocket);
            Check(hr, "MFStartup");
            _started = true;
        }
    }

    static void Check(int hr, string what)
    {
        if (hr < 0)
            throw new InvalidOperationException(AccessDenied(hr, what));
    }

    static string AccessDenied(int hr, string what)
    {
        if (hr == unchecked((int)0x80070005))
            return $"{what} failed (0x80070005 access denied). Windows refused the camera. Close any other app using it, then run Install-WebcamStream.ps1 -Start from an elevated PowerShell. That starts WebcamStream in the signed-in session.";
        return $"{what} failed (0x{hr:X8}).";
    }

    sealed class OpenCamera
    {
        public OpenCamera(int index, string name, IMFActivate activate, IMFMediaSource source, IMFSourceReader reader, List<OpenMode> modes)
        {
            Index = index;
            Name = name;
            Activate = activate;
            Source = source;
            Reader = reader;
            Modes = modes;
        }

        public int Index { get; }
        public string Name { get; }
        public IMFActivate Activate { get; }
        public IMFMediaSource Source { get; }
        public IMFSourceReader Reader { get; }
        public List<OpenMode> Modes { get; }

        public CameraDevice ToInfo() => new(Index, Name, Modes.Select(m => new CameraMode(SubtypeName(m.Subtype), m.Width, m.Height, m.Fps)).ToList());

        public void Release()
        {
            foreach (var mode in Modes)
            {
                try { Marshal.ReleaseComObject(mode.Type); } catch { }
            }
            Modes.Clear();
            try { Source.Shutdown(); } catch { }
            try { Marshal.ReleaseComObject(Reader); } catch { }
            try { Marshal.ReleaseComObject(Source); } catch { }
            try { Activate.ShutdownObject(); } catch { }
            try { Marshal.ReleaseComObject(Activate); } catch { }
        }
    }

    sealed class OpenMode
    {
        public OpenMode(Guid subtype, int width, int height, double fps, IMFMediaType type)
        {
            Subtype = subtype;
            Width = width;
            Height = height;
            Fps = fps;
            Type = type;
        }

        public Guid Subtype { get; }
        public int Width { get; }
        public int Height { get; }
        public double Fps { get; }
        public IMFMediaType Type { get; }
    }

    [DllImport("ole32.dll")] static extern int CoInitializeEx(IntPtr reserved, uint model);
    [DllImport("mfplat.dll")] static extern int MFStartup(int version, int flags);
    [DllImport("mfplat.dll")] static extern int MFCreateAttributes(out IMFAttributes attributes, int initialSize);
    [DllImport("mf.dll")] static extern int MFEnumDeviceSources(IMFAttributes attributes, out IntPtr sources, out int count);
    [DllImport("mfreadwrite.dll")] static extern int MFCreateSourceReaderFromMediaSource(IMFMediaSource source, IMFAttributes attributes, out IMFSourceReader reader);

    [ComImport, Guid("2cd2d921-c447-44a7-a13c-4adabfc247e3"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    interface IMFAttributes
    {
        void GetItem(); void GetItemType(); void CompareItem(); void Compare();
        [PreserveSig] int GetUINT32([MarshalAs(UnmanagedType.LPStruct)] Guid key, out int value);
        [PreserveSig] int GetUINT64([MarshalAs(UnmanagedType.LPStruct)] Guid key, out long value);
        void GetDouble();
        [PreserveSig] int GetGUID([MarshalAs(UnmanagedType.LPStruct)] Guid key, out Guid value);
        void GetStringLength(); void GetString();
        [PreserveSig] int GetAllocatedString([MarshalAs(UnmanagedType.LPStruct)] Guid key, out IntPtr str, out int length);
        void GetBlobSize(); void GetBlob(); void GetAllocatedBlob(); void GetUnknown();
        void SetItem(); void DeleteItem(); void DeleteAllItems();
        [PreserveSig] int SetUINT32([MarshalAs(UnmanagedType.LPStruct)] Guid key, int value);
        [PreserveSig] int SetUINT64([MarshalAs(UnmanagedType.LPStruct)] Guid key, long value);
        void SetDouble();
        [PreserveSig] int SetGUID([MarshalAs(UnmanagedType.LPStruct)] Guid key, [MarshalAs(UnmanagedType.LPStruct)] Guid value);
        void SetString(); void SetBlob(); void SetUnknown(); void LockStore(); void UnlockStore();
        void GetCount(); void GetItemByIndex(); void CopyAllItems();
    }

    [ComImport, Guid("7fee9e9a-4a89-47a6-899c-b6a53a70fb67"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    interface IMFActivate
    {
        void GetItem(); void GetItemType(); void CompareItem(); void Compare();
        void GetUINT32(); void GetUINT64(); void GetDouble(); void GetGUID();
        void GetStringLength(); void GetString();
        [PreserveSig] int GetAllocatedString([MarshalAs(UnmanagedType.LPStruct)] Guid key, out IntPtr str, out int length);
        void GetBlobSize(); void GetBlob(); void GetAllocatedBlob(); void GetUnknown();
        void SetItem(); void DeleteItem(); void DeleteAllItems();
        void SetUINT32(); void SetUINT64(); void SetDouble(); void SetGUID();
        void SetString(); void SetBlob(); void SetUnknown(); void LockStore(); void UnlockStore();
        void GetCount(); void GetItemByIndex(); void CopyAllItems();
        [PreserveSig] int ActivateObject([MarshalAs(UnmanagedType.LPStruct)] Guid riid, out IntPtr ppv);
        [PreserveSig] int ShutdownObject();
        [PreserveSig] int DetachObject();
    }

    [ComImport, Guid("44ae0fa8-ea31-4109-8d2e-4cae4997c555"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    interface IMFMediaType
    {
        void GetItem(); void GetItemType(); void CompareItem(); void Compare();
        [PreserveSig] int GetUINT32([MarshalAs(UnmanagedType.LPStruct)] Guid key, out int value);
        [PreserveSig] int GetUINT64([MarshalAs(UnmanagedType.LPStruct)] Guid key, out long value);
        void GetDouble();
        [PreserveSig] int GetGUID([MarshalAs(UnmanagedType.LPStruct)] Guid key, out Guid value);
        void GetStringLength(); void GetString(); void GetAllocatedString();
        void GetBlobSize(); void GetBlob(); void GetAllocatedBlob(); void GetUnknown();
        void SetItem(); void DeleteItem(); void DeleteAllItems();
        void SetUINT32(); void SetUINT64(); void SetDouble(); void SetGUID();
        void SetString(); void SetBlob(); void SetUnknown(); void LockStore(); void UnlockStore();
        void GetCount(); void GetItemByIndex(); void CopyAllItems();
        void GetMajorType(); void IsCompressedFormat(); void IsEqual(); void GetRepresentation(); void FreeRepresentation();
    }

    [ComImport, Guid("045fa593-8799-42b8-bc8d-8968c6453507"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    interface IMFMediaBuffer
    {
        [PreserveSig] int Lock(out IntPtr buffer, out int maxLength, out int currentLength);
        [PreserveSig] int Unlock();
        void GetCurrentLength(); void SetCurrentLength(); void GetMaxLength();
    }

    [ComImport, Guid("c40a00f2-b93a-4d80-ae8c-5a1c634f58e4"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    interface IMFSample
    {
        void GetItem(); void GetItemType(); void CompareItem(); void Compare();
        void GetUINT32(); void GetUINT64(); void GetDouble(); void GetGUID();
        void GetStringLength(); void GetString(); void GetAllocatedString();
        void GetBlobSize(); void GetBlob(); void GetAllocatedBlob(); void GetUnknown();
        void SetItem(); void DeleteItem(); void DeleteAllItems();
        void SetUINT32(); void SetUINT64(); void SetDouble(); void SetGUID();
        void SetString(); void SetBlob(); void SetUnknown(); void LockStore(); void UnlockStore();
        void GetCount(); void GetItemByIndex(); void CopyAllItems();
        void GetSampleFlags(); void SetSampleFlags(); void GetSampleTime(); void SetSampleTime();
        void GetSampleDuration(); void SetSampleDuration(); void GetBufferCount(); void GetBufferByIndex();
        [PreserveSig] int ConvertToContiguousBuffer(out IMFMediaBuffer buffer);
    }

    [ComImport, Guid("279a808d-aec7-40c8-9c6b-a6b492c78a66"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    interface IMFMediaSource
    {
        void GetEvent(); void BeginGetEvent(); void EndGetEvent(); void QueueEvent();
        void GetCharacteristics(); void CreatePresentationDescriptor(); void Start(); void Stop(); void Pause();
        [PreserveSig] int Shutdown();
    }

    [ComImport, Guid("70ae66f2-c809-4e4f-8915-bdcb406b7993"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    interface IMFSourceReader
    {
        void GetStreamSelection();
        [PreserveSig] int SetStreamSelection(int index, [MarshalAs(UnmanagedType.Bool)] bool selected);
        [PreserveSig] int GetNativeMediaType(int streamIndex, int typeIndex, out IMFMediaType type);
        void GetCurrentMediaType();
        [PreserveSig] int SetCurrentMediaType(int streamIndex, IntPtr reserved, IMFMediaType type);
        void SetCurrentPosition();
        [PreserveSig] int ReadSample(int streamIndex, int control, out int actual, out int flags, out long timestamp, out IntPtr sample);
        void Flush(); void GetServiceForStream(); void GetPresentationAttribute();
    }
}
