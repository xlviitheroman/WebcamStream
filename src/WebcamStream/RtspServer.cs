using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;

namespace WebcamStream;

sealed class RtspServer : IDisposable
{
    readonly AppConfig _config;
    readonly AccessControl _access;
    readonly TcpListener _listener;
    readonly CancellationTokenSource _stop = new();
    readonly List<Client> _clients = new();
    readonly object _clientsGate = new();
    readonly object _portGate = new();
    readonly HashSet<int> _usedRtpPorts = new();
    VideoFrame? _latest;
    int _frameSerial;
    Thread? _acceptThread;
    Thread? _sendThread;
    int _clientCount;

    public RtspServer(AppConfig config, AccessControl access)
    {
        _config = config;
        _access = access;
        var ip = IPAddress.Parse(config.ListenAddress);
        _listener = new TcpListener(ip, config.Port);
    }

    public int Port { get; private set; }
    public int ClientCount => Volatile.Read(ref _clientCount);

    public void Start()
    {
        _listener.Start();
        Port = ((IPEndPoint)_listener.LocalEndpoint).Port;
        _acceptThread = new Thread(AcceptLoop) { IsBackground = true, Name = "rtsp-accept" };
        _sendThread = new Thread(SendLoop) { IsBackground = true, Name = "rtsp-send" };
        _acceptThread.Start();
        _sendThread.Start();
    }

    public void Publish(VideoFrame frame)
    {
        _latest = frame;
        _frameSerial = frame.Id;
    }

    public VideoFrame? Latest => _latest;

    void AcceptLoop()
    {
        while (!_stop.IsCancellationRequested)
        {
            TcpClient? tcp = null;
            try
            {
                tcp = _listener.AcceptTcpClient();
                tcp.NoDelay = true;
                var remote = (IPEndPoint)tcp.Client.RemoteEndPoint!;
                if (!_access.Permits(remote.Address))
                {
                    Log.Warn($"Rejected {remote.Address} (not in the allow list).");
                    tcp.Close();
                    continue;
                }
                if (ClientCount >= _config.MaxClients)
                {
                    Log.Warn($"Rejected {remote.Address} (already {_config.MaxClients} clients).");
                    try { SendRaw(tcp.GetStream(), "RTSP/1.0 503 Service Unavailable\r\nCSeq: 0\r\n\r\n"); } catch { }
                    tcp.Close();
                    continue;
                }
                var client = new Client(tcp, remote);
                lock (_clientsGate) _clients.Add(client);
                Interlocked.Increment(ref _clientCount);
                Log.Info($"Client connected from {remote.Address}");
                var thread = new Thread(() => ClientLoop(client)) { IsBackground = true, Name = "rtsp-client" };
                thread.Start();
            }
            catch (SocketException) when (_stop.IsCancellationRequested)
            {
                break;
            }
            catch (ObjectDisposedException)
            {
                break;
            }
            catch (Exception ex)
            {
                Log.Error($"Accept failed: {ex.Message}");
                try { tcp?.Close(); } catch { }
            }
        }
    }

    void SendLoop()
    {
        var clock = System.Diagnostics.Stopwatch.StartNew();
        long next = 0;
        while (!_stop.IsCancellationRequested)
        {
            int fps = Math.Clamp(_config.Fps, 1, 15);
            int interval = Math.Max(1, 1000 / fps);
            long now = clock.ElapsedMilliseconds;
            if (now < next)
            {
                Thread.Sleep((int)Math.Min(50, next - now));
                continue;
            }
            next = now + interval;
            var frame = _latest;
            if (frame == null)
                continue;
            uint timestamp = (uint)(clock.ElapsedMilliseconds * (RtpJpeg.Clock / 1000));
            List<Client> snapshot;
            lock (_clientsGate) snapshot = _clients.ToList();
            foreach (var client in snapshot)
            {
                if (!client.Playing || client.Closed) continue;
                try { SendFrame(client, frame, timestamp); }
                catch (Exception ex)
                {
                    Log.Warn($"Dropped client {client.Remote.Address}: {ex.Message}");
                    CloseClient(client);
                }
            }
            if (clock.ElapsedMilliseconds - _lastReport > 5000)
            {
                _lastReport = clock.ElapsedMilliseconds;
                foreach (var client in snapshot)
                {
                    if (!client.Playing || client.Closed) continue;
                    try { SendReport(client, timestamp); }
                    catch { CloseClient(client); }
                }
            }
        }
    }

    long _lastReport;

    void ClientLoop(Client client)
    {
        try
        {
            var stream = client.Tcp.GetStream();
            stream.ReadTimeout = 5000;
            while (!_stop.IsCancellationRequested && !client.Closed)
            {
                int first;
                try { first = stream.ReadByte(); }
                catch (IOException)
                {
                    if (DateTime.UtcNow - client.LastActivity > TimeSpan.FromSeconds(60))
                        break;
                    continue;
                }
                if (first < 0) break;
                if (first == 0x24)
                {
                    if (!DiscardInterleaved(stream)) break;
                    client.Touch();
                    continue;
                }
                var text = ReadHeaders(stream, (byte)first);
                if (text == null) break;
                client.Touch();
                Handle(client, stream, text);
                if (client.TimedOut)
                    break;
            }
        }
        catch (Exception ex) when (ex is IOException or SocketException or ObjectDisposedException)
        {
        }
        catch (Exception ex)
        {
            Log.Warn($"Client {client.Remote.Address} ended: {ex.Message}");
        }
        finally
        {
            CloseClient(client);
        }
    }

    void Handle(Client client, NetworkStream stream, string text)
    {
        var lines = text.Split("\r\n");
        if (lines.Length == 0) return;
        var parts = lines[0].Split(' ', 3, StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length < 2) return;
        string method = parts[0].ToUpperInvariant();
        string url = parts[1];
        var headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        for (int i = 1; i < lines.Length; i++)
        {
            int colon = lines[i].IndexOf(':');
            if (colon <= 0) continue;
            headers[lines[i][..colon].Trim()] = lines[i][(colon + 1)..].Trim();
        }
        headers.TryGetValue("CSeq", out var cseq);
        cseq ??= "0";
        if (headers.TryGetValue("Content-Length", out var lenText) && int.TryParse(lenText, out var len) && len > 0)
        {
            var sink = new byte[Math.Min(len, 65536)];
            int got = 0;
            while (got < len && got < sink.Length)
            {
                int n = stream.Read(sink, got, Math.Min(sink.Length - got, len - got));
                if (n <= 0) break;
                got += n;
            }
        }

        if (DateTime.UtcNow - client.LastActivity > TimeSpan.FromSeconds(70) && method != "OPTIONS" && method != "DESCRIBE")
        {
            client.TimedOut = true;
        }

        bool authed = string.IsNullOrEmpty(_config.Username) || method == "OPTIONS" || Authorized(headers);
        if (!authed)
        {
            Respond(client, cseq, 401, "Unauthorized", "WWW-Authenticate: Basic realm=\"WebcamStream\"\r\n");
            return;
        }

        switch (method)
        {
            case "OPTIONS":
                Respond(client, cseq, 200, "OK", "Public: OPTIONS, DESCRIBE, SETUP, PLAY, PAUSE, TEARDOWN, GET_PARAMETER, SET_PARAMETER\r\n");
                break;
            case "DESCRIBE":
                if (!PathOk(url)) { Respond(client, cseq, 404, "Not Found", ""); break; }
                var sdp = BuildSdp(client);
                var body = Encoding.ASCII.GetBytes(sdp);
                var extra = $"Content-Type: application/sdp\r\nContent-Base: {BaseUrl(client)}\r\nContent-Length: {body.Length}\r\n";
                Respond(client, cseq, 200, "OK", extra, body);
                break;
            case "SETUP":
                if (!PathOk(url)) { Respond(client, cseq, 404, "Not Found", ""); break; }
                if (!headers.TryGetValue("Transport", out var transport))
                {
                    Respond(client, cseq, 400, "Bad Request", "");
                    break;
                }
                Setup(client, cseq, transport);
                break;
            case "PLAY":
                if (client.SessionId == null) { Respond(client, cseq, 454, "Session Not Found", ""); break; }
                client.Playing = true;
                client.Paused = false;
                Respond(client, cseq, 200, "OK",
                    $"Session: {client.SessionId};timeout=60\r\nRange: npt=0.000-\r\nRTP-Info: url={BaseUrl(client)}track1;seq={client.Sequence};rtptime={client.Timestamp}\r\n");
                Log.Info($"Playing for {client.Remote.Address}");
                break;
            case "PAUSE":
                client.Playing = false;
                client.Paused = true;
                Respond(client, cseq, 200, "OK", client.SessionId == null ? "" : $"Session: {client.SessionId}\r\n");
                break;
            case "TEARDOWN":
                Respond(client, cseq, 200, "OK", client.SessionId == null ? "" : $"Session: {client.SessionId}\r\n");
                CloseClient(client);
                break;
            case "GET_PARAMETER":
            case "SET_PARAMETER":
                Respond(client, cseq, 200, "OK",
                    (client.SessionId == null ? "" : $"Session: {client.SessionId}\r\n") + "Content-Length: 0\r\n");
                break;
            default:
                Respond(client, cseq, 405, "Method Not Allowed", "Public: OPTIONS, DESCRIBE, SETUP, PLAY, PAUSE, TEARDOWN, GET_PARAMETER, SET_PARAMETER\r\n");
                break;
        }
    }

    void Setup(Client client, string cseq, string transport)
    {
        bool tcp = transport.Contains("TCP", StringComparison.OrdinalIgnoreCase)
                   || transport.Contains("interleaved", StringComparison.OrdinalIgnoreCase);
        if (tcp)
        {
            client.UseTcp = true;
            client.RtpChannel = 0;
            client.RtcpChannel = 1;
            client.SessionId ??= NewSession();
            Respond(client, cseq, 200, "OK",
                $"Session: {client.SessionId};timeout=60\r\nTransport: RTP/AVP/TCP;unicast;interleaved=0-1\r\n");
            return;
        }

        if (!TryClientPorts(transport, out int cRtp, out int cRtcp))
        {
            Respond(client, cseq, 461, "Unsupported Transport", "");
            return;
        }
        if (!TryBindServerPorts(out int sRtp, out int sRtcp, out var udp, out var udpRtcp))
        {
            Respond(client, cseq, 503, "Service Unavailable", "");
            return;
        }
        client.Udp?.Dispose();
        client.UdpRtcp?.Dispose();
        client.Udp = udp;
        client.UdpRtcp = udpRtcp;
        client.UseTcp = false;
        client.ClientRtpPort = cRtp;
        client.ClientRtcpPort = cRtcp;
        client.ServerRtpPort = sRtp;
        client.ServerRtcpPort = sRtcp;
        client.SessionId ??= NewSession();
        var source = ((IPEndPoint)client.Tcp.Client.LocalEndPoint!).Address;
        Respond(client, cseq, 200, "OK",
            $"Session: {client.SessionId};timeout=60\r\nTransport: RTP/AVP;unicast;client_port={cRtp}-{cRtcp};server_port={sRtp}-{sRtcp};source={source}\r\n");
    }

    bool TryBindServerPorts(out int rtp, out int rtcp, out UdpClient udp, out UdpClient udpRtcp)
    {
        rtp = rtcp = 0;
        udp = null!;
        udpRtcp = null!;
        int start = _config.RtpPortStart;
        int pairs = _config.RtpPortCount;
        if ((start & 1) == 1) start++;
        lock (_portGate)
        {
            for (int i = 0; i < pairs; i++)
            {
                int port = start + i * 2;
                if (port > 65533) break;
                if (_usedRtpPorts.Contains(port)) continue;
                UdpClient? rtpSocket = null;
                UdpClient? rtcpSocket = null;
                try
                {
                    rtpSocket = new UdpClient(port);
                    rtcpSocket = new UdpClient(port + 1);
                    _usedRtpPorts.Add(port);
                    rtp = port;
                    rtcp = port + 1;
                    udp = rtpSocket;
                    udpRtcp = rtcpSocket;
                    return true;
                }
                catch (SocketException)
                {
                    try { rtpSocket?.Dispose(); } catch { }
                    try { rtcpSocket?.Dispose(); } catch { }
                }
            }
        }
        return false;
    }

    static bool TryClientPorts(string transport, out int rtp, out int rtcp)
    {
        rtp = rtcp = 0;
        int idx = transport.IndexOf("client_port", StringComparison.OrdinalIgnoreCase);
        if (idx < 0) return false;
        int eq = transport.IndexOf('=', idx);
        if (eq < 0) return false;
        var value = transport[(eq + 1)..];
        int semi = value.IndexOf(';');
        if (semi >= 0) value = value[..semi];
        var pair = value.Split('-', 2);
        if (!int.TryParse(pair[0].Trim(), out rtp)) return false;
        if (pair.Length == 1) rtcp = rtp + 1;
        else if (!int.TryParse(pair[1].Trim(), out rtcp)) return false;
        return rtp is > 0 and < 65535 && rtcp is > 0 and <= 65535;
    }

    void SendFrame(Client client, VideoFrame frame, uint timestamp)
    {
        client.Timestamp = timestamp;
        foreach (var slice in frame.Slices)
        {
            int seq = client.Sequence++ & 0xFFFF;
            var packet = RtpJpeg.WriteHeader(seq, timestamp, slice.Marker, client.Ssrc, slice.Payload.Length);
            Buffer.BlockCopy(slice.Payload, 0, packet, 12, slice.Payload.Length);
            WritePacket(client, packet, rtcp: false);
            client.Packets++;
            client.Octets += (uint)slice.Payload.Length;
        }
    }

    void SendReport(Client client, uint timestamp)
    {
        var report = RtpJpeg.SenderReport(client.Ssrc, timestamp, client.Packets, client.Octets);
        WritePacket(client, report, rtcp: true);
    }

    void WritePacket(Client client, byte[] packet, bool rtcp)
    {
        if (client.UseTcp)
        {
            int channel = rtcp ? client.RtcpChannel : client.RtpChannel;
            var wrapped = new byte[4 + packet.Length];
            wrapped[0] = 0x24;
            wrapped[1] = (byte)channel;
            wrapped[2] = (byte)((packet.Length >> 8) & 0xFF);
            wrapped[3] = (byte)(packet.Length & 0xFF);
            Buffer.BlockCopy(packet, 0, wrapped, 4, packet.Length);
            lock (client.WriteLock)
            {
                var stream = client.Tcp.GetStream();
                stream.Write(wrapped, 0, wrapped.Length);
            }
            return;
        }
        var socket = rtcp ? client.UdpRtcp : client.Udp;
        if (socket == null) return;
        int port = rtcp ? client.ClientRtcpPort : client.ClientRtpPort;
        var dest = new IPEndPoint(client.Remote.Address, port);
        socket.Send(packet, packet.Length, dest);
    }

    bool PathOk(string url)
    {
        string path = url;
        int scheme = path.IndexOf("://", StringComparison.Ordinal);
        if (scheme >= 0)
        {
            int slash = path.IndexOf('/', scheme + 3);
            path = slash < 0 ? "/" : path[slash..];
        }
        int q = path.IndexOf('?');
        if (q >= 0) path = path[..q];
        if (path.EndsWith("/track1", StringComparison.OrdinalIgnoreCase))
            path = path[..^"/track1".Length];
        if (path.Length > 1) path = path.TrimEnd('/');
        if (path.Length == 0) path = "/";
        var configured = _config.Path;
        if (configured.Length > 1) configured = configured.TrimEnd('/');
        return path.Equals(configured, StringComparison.OrdinalIgnoreCase);
    }

    string BuildSdp(Client client)
    {
        var local = ((IPEndPoint)client.Tcp.Client.LocalEndPoint!).Address;
        var frame = _latest;
        int fps = Math.Clamp(_config.Fps, 1, 15);
        return
            "v=0\r\n" +
            $"o=- 0 1 IN IP4 {local}\r\n" +
            "s=WebcamStream\r\n" +
            "i=720p webcam, a few frames per second, no re-encode when the camera offers MJPEG\r\n" +
            "t=0 0\r\n" +
            "a=tool:WebcamStream\r\n" +
            "a=control:*\r\n" +
            "a=range:npt=now-\r\n" +
            "m=video 0 RTP/AVP 26\r\n" +
            "c=IN IP4 0.0.0.0\r\n" +
            "a=rtpmap:26 JPEG/90000\r\n" +
            $"a=framerate:{fps}\r\n" +
            "a=control:track1\r\n" +
            (frame == null ? "" : $"a=x-dimensions:{frame.Width},{frame.Height}\r\n");
    }

    string BaseUrl(Client client)
    {
        var local = (IPEndPoint)client.Tcp.Client.LocalEndPoint!;
        var path = _config.Path.TrimEnd('/');
        return $"rtsp://{local.Address}:{local.Port}{path}/";
    }

    bool Authorized(Dictionary<string, string> headers)
    {
        if (!headers.TryGetValue("Authorization", out var value))
            return false;
        const string prefix = "Basic ";
        if (!value.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
            return false;
        string decoded;
        try { decoded = Encoding.UTF8.GetString(Convert.FromBase64String(value[prefix.Length..].Trim())); }
        catch { return false; }
        int colon = decoded.IndexOf(':');
        if (colon < 0) return false;
        var user = decoded[..colon];
        var pass = decoded[(colon + 1)..];
        return FixedEquals(user, _config.Username) && FixedEquals(pass, _config.Password);
    }

    static bool FixedEquals(string a, string b)
    {
        var aa = Encoding.UTF8.GetBytes(a);
        var bb = Encoding.UTF8.GetBytes(b);
        return CryptographicOperations.FixedTimeEquals(
            Fit(aa, 256), Fit(bb, 256)) && aa.Length == bb.Length;
    }

    static byte[] Fit(byte[] src, int len)
    {
        var dst = new byte[len];
        Buffer.BlockCopy(src, 0, dst, 0, Math.Min(src.Length, len));
        return dst;
    }

    void Respond(Client client, string cseq, int code, string reason, string extra, byte[]? body = null)
    {
        var head = $"RTSP/1.0 {code} {reason}\r\nCSeq: {cseq}\r\nServer: WebcamStream\r\n{extra}\r\n";
        var bytes = Encoding.ASCII.GetBytes(head);
        lock (client.WriteLock)
        {
            var stream = client.Tcp.GetStream();
            stream.Write(bytes, 0, bytes.Length);
            if (body != null && body.Length > 0)
                stream.Write(body, 0, body.Length);
        }
    }

    static void SendRaw(NetworkStream stream, string text)
    {
        var bytes = Encoding.ASCII.GetBytes(text);
        stream.Write(bytes, 0, bytes.Length);
    }

    static string? ReadHeaders(NetworkStream stream, byte first)
    {
        var buf = new List<byte>(256) { first };
        while (buf.Count < 16384)
        {
            int b = stream.ReadByte();
            if (b < 0) return null;
            buf.Add((byte)b);
            int n = buf.Count;
            if (n >= 4 && buf[n - 4] == '\r' && buf[n - 3] == '\n' && buf[n - 2] == '\r' && buf[n - 1] == '\n')
                return Encoding.ASCII.GetString(buf.ToArray(), 0, n - 4);
        }
        return null;
    }

    static bool DiscardInterleaved(NetworkStream stream)
    {
        int channel = stream.ReadByte();
        int hi = stream.ReadByte();
        int lo = stream.ReadByte();
        if (channel < 0 || hi < 0 || lo < 0) return false;
        int len = (hi << 8) | lo;
        if (len < 0 || len > 1_000_000) return false;
        var sink = new byte[Math.Min(len, 8192)];
        int left = len;
        while (left > 0)
        {
            int n = stream.Read(sink, 0, Math.Min(sink.Length, left));
            if (n <= 0) return false;
            left -= n;
        }
        return true;
    }

    static string NewSession() => Convert.ToHexString(RandomNumberGenerator.GetBytes(8));

    void CloseClient(Client client)
    {
        if (client.Closed) return;
        client.Closed = true;
        client.Playing = false;
        try { client.Tcp.Close(); } catch { }
        try { client.Udp?.Dispose(); } catch { }
        try { client.UdpRtcp?.Dispose(); } catch { }
        if (client.ServerRtpPort != 0)
        {
            lock (_portGate) _usedRtpPorts.Remove(client.ServerRtpPort);
        }
        bool removed;
        lock (_clientsGate) removed = _clients.Remove(client);
        if (removed)
        {
            Interlocked.Decrement(ref _clientCount);
            Log.Info($"Client disconnected {client.Remote.Address}");
        }
    }

    public void Dispose()
    {
        _stop.Cancel();
        try { _listener.Stop(); } catch { }
        List<Client> snapshot;
        lock (_clientsGate) snapshot = _clients.ToList();
        foreach (var client in snapshot) CloseClient(client);
        _acceptThread?.Join(1000);
        _sendThread?.Join(1000);
        _stop.Dispose();
    }

    sealed class Client
    {
        public Client(TcpClient tcp, IPEndPoint remote)
        {
            Tcp = tcp;
            Remote = remote;
            Ssrc = BitConverter.ToUInt32(RandomNumberGenerator.GetBytes(4));
            LastActivity = DateTime.UtcNow;
        }

        public TcpClient Tcp { get; }
        public IPEndPoint Remote { get; }
        public object WriteLock { get; } = new();
        public string? SessionId { get; set; }
        public bool Playing;
        public bool Paused;
        public bool Closed;
        public bool TimedOut;
        public bool UseTcp = true;
        public int RtpChannel;
        public int RtcpChannel = 1;
        public int ClientRtpPort;
        public int ClientRtcpPort;
        public int ServerRtpPort;
        public int ServerRtcpPort;
        public UdpClient? Udp;
        public UdpClient? UdpRtcp;
        public uint Ssrc;
        public int Sequence = 1;
        public uint Timestamp;
        public uint Packets;
        public uint Octets;
        public DateTime LastActivity;

        public void Touch() => LastActivity = DateTime.UtcNow;
    }
}
