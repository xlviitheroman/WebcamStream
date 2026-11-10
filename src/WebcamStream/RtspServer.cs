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
    readonly string _nonce = Convert.ToHexString(RandomNumberGenerator.GetBytes(16));
    const string Realm = "WebcamStream";
    VideoFrame? _latest;
    long _packetsSent;
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
    public long PacketsSent => Interlocked.Read(ref _packetsSent);

    public void Start()
    {
        _listener.Start();
        Port = ((IPEndPoint)_listener.LocalEndpoint).Port;
        _acceptThread = new Thread(AcceptLoop) { IsBackground = true, Name = "rtsp-accept" };
        _sendThread = new Thread(SendLoop) { IsBackground = true, Name = "rtsp-send" };
        _acceptThread.Start();
        _sendThread.Start();
    }

    public void Publish(VideoFrame frame) => Volatile.Write(ref _latest, frame);

    public VideoFrame? Latest => Volatile.Read(ref _latest);

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
                Log.Info($"Client connected from {remote.Address}:{remote.Port}");
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
            var frame = Volatile.Read(ref _latest);
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
                    // A recorder that is already playing keeps this socket quiet between
                    // pictures. That socket stays up. A socket that never reached PLAY
                    // is only the login attempt. Holding it for minutes is why Exacq's
                    // log shows one connection and then stops opening another.
                    if (!client.Playing && DateTime.UtcNow - client.LastActivity > TimeSpan.FromSeconds(15))
                    {
                        client.QuietTimeout = true;
                        break;
                    }
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
        var lines = Unfold(text);
        if (lines.Count == 0) return;
        var parts = lines[0].Split(' ', 3, StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length < 2) return;
        string method = parts[0].ToUpperInvariant();
        string url = parts[1];
        var headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        for (int i = 1; i < lines.Count; i++)
        {
            int colon = lines[i].IndexOf(':');
            if (colon <= 0) continue;
            headers[lines[i][..colon].Trim()] = lines[i][(colon + 1)..].Trim();
        }
        headers.TryGetValue("CSeq", out var cseq);
        cseq ??= "0";
        client.Requests++;
        client.LastMethod = method;
        client.SentCredentials |= headers.ContainsKey("Authorization") || UrlHasUserInfo(url);
        if (client.Requests == 1 && headers.TryGetValue("User-Agent", out var agent) && agent.Length > 0)
            Log.Info($"Recorder at {client.Remote.Address}:{client.Remote.Port} identifies as {Shown(agent)}.");
        string login = AuthNote(headers, url);
        Log.Info($"RTSP {method} from {client.Remote.Address}:{client.Remote.Port} cseq {cseq}, {login}, url {Redact(url)}");
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

        string why = "";
        bool authed = method == "OPTIONS" || Authorized(headers, url, method, out why);
        if (!authed)
        {
            bool staleNonce = why == "digest nonce was not the one this server sent";
            string stale = staleNonce ? ", stale=\"true\"" : "";
            Log.Info($"Answered 401 to {method} from {client.Remote.Address}:{client.Remote.Port}: {why}.");
            // qop is advertised, and a response that ignores qop is still accepted.
            // Some recorders require qop in the challenge before they will answer.
            // Others answer qop with a hash that leaves qop out.
            Respond(client, cseq, 401, "Unauthorized",
                $"WWW-Authenticate: Digest realm=\"{Realm}\", nonce=\"{_nonce}\", algorithm=\"MD5\", qop=\"auth\"{stale}\r\n" +
                $"WWW-Authenticate: Basic realm=\"{Realm}\"\r\n");
            return;
        }

        switch (method)
        {
            case "OPTIONS":
                Respond(client, cseq, 200, "OK", "Public: OPTIONS, DESCRIBE, SETUP, PLAY, PAUSE, TEARDOWN, GET_PARAMETER, SET_PARAMETER\r\n");
                break;
            case "DESCRIBE":
                RememberPresentation(client, url, headers);
                Log.Info($"RTSP DESCRIBE {Redact(url)}");
                var sdp = BuildSdp(client);
                var body = Encoding.ASCII.GetBytes(sdp);
                var extra = $"Content-Type: application/sdp\r\nContent-Base: {client.PresentationUrl}\r\nContent-Length: {body.Length}\r\n";
                Respond(client, cseq, 200, "OK", extra, body);
                break;
            case "SETUP":
                RememberPresentation(client, url, headers);
                Log.Info($"RTSP SETUP {Redact(url)}");
                if (!headers.TryGetValue("Transport", out var transport))
                {
                    Respond(client, cseq, 400, "Bad Request", "");
                    break;
                }
                Setup(client, cseq, transport);
                break;
            case "PLAY":
                if (client.SessionId == null) { Respond(client, cseq, 454, "Session Not Found", ""); break; }
                RememberPresentation(client, url, headers);
                client.Playing = true;
                client.Paused = false;
                Respond(client, cseq, 200, "OK",
                    $"Session: {client.SessionId};timeout=600\r\nRange: npt=0.000-\r\nRTP-Info: url={client.PresentationUrl}track1;seq={client.Sequence};rtptime={client.Timestamp}\r\n");
                Log.Info($"RTSP PLAY {Redact(url)}");
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
            TryInterleaved(transport, out int rtpChannel, out int rtcpChannel);
            client.RtpChannel = rtpChannel;
            client.RtcpChannel = rtcpChannel;
            client.SessionId ??= NewSession();
            Respond(client, cseq, 200, "OK",
                $"Session: {client.SessionId};timeout=600\r\nTransport: RTP/AVP/TCP;unicast;interleaved={rtpChannel}-{rtcpChannel}\r\n");
            Log.Info($"Viewer {client.Remote.Address} is receiving RTP inside the RTSP connection, channels {rtpChannel}-{rtcpChannel}.");
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
        var source = Unmap(((IPEndPoint)client.Tcp.Client.LocalEndPoint!).Address);
        Respond(client, cseq, 200, "OK",
            $"Session: {client.SessionId};timeout=600\r\nTransport: RTP/AVP;unicast;client_port={cRtp}-{cRtcp};server_port={sRtp}-{sRtcp};source={source}\r\n");
        Log.Info($"Viewer {client.Remote.Address} is receiving RTP over UDP. This PC sends to {Unmap(client.Remote.Address)}:{cRtp}. Server ports are {sRtp}-{sRtcp}.");
    }

    static void TryInterleaved(string transport, out int rtp, out int rtcp)
    {
        rtp = 0;
        rtcp = 1;
        int idx = transport.IndexOf("interleaved", StringComparison.OrdinalIgnoreCase);
        if (idx < 0) return;
        int eq = transport.IndexOf('=', idx);
        if (eq < 0) return;
        var value = transport[(eq + 1)..];
        int semi = value.IndexOf(';');
        if (semi >= 0) value = value[..semi];
        var pair = value.Split('-', 2);
        if (!int.TryParse(pair[0].Trim(), out int first)) return;
        int second = first + 1;
        if (pair.Length > 1 && !int.TryParse(pair[1].Trim(), out second)) return;
        if (first is < 0 or > 255 || second is < 0 or > 255) return;
        rtp = first;
        rtcp = second;
    }

    static IPAddress Unmap(IPAddress address) =>
        address.IsIPv4MappedToIPv6 ? address.MapToIPv4() : address;

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
        client.Touch();
        foreach (var slice in frame.Slices)
        {
            int seq = client.Sequence++ & 0xFFFF;
            var packet = RtpJpeg.WriteHeader(seq, timestamp, slice.Marker, client.Ssrc, slice.Payload.Length);
            Buffer.BlockCopy(slice.Payload, 0, packet, 12, slice.Payload.Length);
            WritePacket(client, packet, rtcp: false);
            client.Packets++;
            client.Octets += (uint)slice.Payload.Length;
            Interlocked.Increment(ref _packetsSent);
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
        var dest = new IPEndPoint(Unmap(client.Remote.Address), port);
        socket.Send(packet, packet.Length, dest);
    }

    void RememberPresentation(Client client, string url, Dictionary<string, string> headers)
    {
        if (!string.IsNullOrEmpty(client.PresentationUrl))
            return;
        client.PresentationUrl = PresentationBase(client, url, headers);
    }

    string PresentationBase(Client client, string url, Dictionary<string, string> headers)
    {
        var local = (IPEndPoint)client.Tcp.Client.LocalEndPoint!;
        string authority = AuthorityOf(url);
        if (authority.Length == 0 && headers.TryGetValue("Host", out var host))
            authority = host.Trim();
        if (authority.Length == 0)
            authority = $"{Unmap(local.Address)}:{local.Port}";
        authority = WithPort(authority, local.Port);
        var path = PresentationPath(url);
        if (path == "/")
            return $"rtsp://{authority}/";
        return $"rtsp://{authority}{path}/";
    }

    static string AuthorityOf(string url)
    {
        int scheme = url.IndexOf("://", StringComparison.Ordinal);
        if (scheme < 0) return "";
        int start = scheme + 3;
        int slash = url.IndexOf('/', start);
        string host = slash < 0 ? url[start..] : url[start..slash];
        int at = host.LastIndexOf('@');
        if (at >= 0) host = host[(at + 1)..];
        int q = host.IndexOf('?');
        if (q >= 0) host = host[..q];
        return host;
    }

    static string WithPort(string authority, int port)
    {
        if (authority.StartsWith('['))
            return authority.Contains("]:", StringComparison.Ordinal) ? authority : $"{authority}:{port}";
        int colon = authority.LastIndexOf(':');
        if (colon > 0 && int.TryParse(authority[(colon + 1)..], out _))
            return authority;
        return $"{authority}:{port}";
    }

    static string PresentationPath(string url)
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
        string[] suffixes = { "/track1", "/trackID=0", "/trackID=1", "/trackID=2", "/video", "/audio" };
        foreach (var suffix in suffixes)
        {
            if (path.EndsWith(suffix, StringComparison.OrdinalIgnoreCase))
            {
                path = path[..^suffix.Length];
                break;
            }
        }
        if (path.Length > 1) path = path.TrimEnd('/');
        if (path.Length == 0) path = "/";
        return path;
    }

    string BuildSdp(Client client)
    {
        var local = Unmap(((IPEndPoint)client.Tcp.Client.LocalEndPoint!).Address);
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
            "a=sendonly\r\n" +
            $"a=framerate:{fps}\r\n" +
            "a=control:track1\r\n" +
            (frame == null ? "" : $"a=x-dimensions:{frame.Width},{frame.Height}\r\n");
    }

    bool Authorized(Dictionary<string, string> headers, string url, string method, out string why)
    {
        why = "no username or password was sent";
        if (headers.TryGetValue("Authorization", out var value))
        {
            if (value.StartsWith("Basic ", StringComparison.OrdinalIgnoreCase))
            {
                if (BasicOk(value["Basic ".Length..].Trim(), out why)) return true;
                return false;
            }
            if (value.StartsWith("Digest ", StringComparison.OrdinalIgnoreCase))
            {
                if (DigestOk(value["Digest ".Length..], method, url, out why)) return true;
                return false;
            }
            why = "authorization was not Basic or Digest";
            return false;
        }
        if (UserInfoOk(url)) return true;
        return false;
    }

    bool BasicOk(string token, out string why)
    {
        why = "username or password was not accepted";
        string decoded;
        try { decoded = Encoding.UTF8.GetString(Convert.FromBase64String(token.Trim())); }
        catch
        {
            why = "basic login could not be read";
            return false;
        }
        int colon = decoded.IndexOf(':');
        if (colon < 0) return false;
        string user = decoded[..colon].Trim();
        if (!FixedEquals(user, _config.Username))
        {
            why = $"username \"{Shown(user)}\" was not accepted";
            return false;
        }
        if (!Credentials(user, decoded[(colon + 1)..]))
        {
            why = "password was not accepted";
            return false;
        }
        return true;
    }

    bool DigestOk(string value, string method, string requestUrl, out string why)
    {
        why = "digest login could not be read";
        var fields = ParseDigest(value);
        if (!fields.TryGetValue("username", out var user)) return false;
        user = user.Trim();
        if (!FixedEquals(user, _config.Username))
        {
            why = $"username \"{Shown(user)}\" was not accepted";
            return false;
        }
        if (fields.TryGetValue("realm", out var realm) && realm.Length > 0 && !string.Equals(realm, Realm, StringComparison.Ordinal))
        {
            why = $"digest realm \"{Shown(realm)}\" was not {Realm}";
            return false;
        }
        if (!fields.TryGetValue("nonce", out var nonce) || !HexEquals(nonce.Trim(), _nonce))
        {
            why = "digest nonce was not the one this server sent";
            return false;
        }
        if (!fields.TryGetValue("uri", out var uri) || !fields.TryGetValue("response", out var response))
            return false;

        string ha1 = Md5Hex($"{_config.Username}:{Realm}:{_config.Password}");
        if (fields.TryGetValue("algorithm", out var algorithm) && algorithm.Equals("MD5-sess", StringComparison.OrdinalIgnoreCase)
            && fields.TryGetValue("cnonce", out var sessNonce))
            ha1 = Md5Hex($"{ha1}:{_nonce}:{sessNonce}");
        var uris = new List<string>();
        void AddUri(string candidate)
        {
            candidate = candidate.Trim();
            if (candidate.Length == 0) return;
            if (uris.Exists(item => item.Equals(candidate, StringComparison.Ordinal))) return;
            uris.Add(candidate);
        }
        AddUri(uri);
        AddUri(uri.TrimEnd('/'));
        AddUri(uri.EndsWith('/') ? uri : uri + "/");
        AddUri(Redact(requestUrl));
        fields.TryGetValue("nc", out var nc);
        fields.TryGetValue("cnonce", out var cnonce);
        bool haveNc = !string.IsNullOrEmpty(nc) && !string.IsNullOrEmpty(cnonce);
        string qop = "auth";
        if (fields.TryGetValue("qop", out var sent) && sent.Length > 0)
            qop = sent.Split(',')[0].Trim();
        foreach (var candidate in uris)
        {
            string ha2 = Md5Hex($"{method}:{candidate}");
            if (HexEquals(response.Trim(), Md5Hex($"{ha1}:{_nonce}:{ha2}"))) return true;
            if (haveNc && qop.Equals("auth", StringComparison.OrdinalIgnoreCase)
                && HexEquals(response.Trim(), Md5Hex($"{ha1}:{_nonce}:{nc}:{cnonce}:{qop}:{ha2}")))
                return true;
        }
        why = "digest password was not accepted";
        return false;
    }

    static Dictionary<string, string> ParseDigest(string value)
    {
        var map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        int i = 0;
        while (i < value.Length)
        {
            while (i < value.Length && (value[i] == ' ' || value[i] == ',')) i++;
            if (i >= value.Length) break;
            int eq = value.IndexOf('=', i);
            if (eq < 0) break;
            string key = value[i..eq].Trim();
            i = eq + 1;
            string item;
            if (i < value.Length && value[i] == '"')
            {
                int end = value.IndexOf('"', i + 1);
                if (end < 0) break;
                item = value[(i + 1)..end];
                i = end + 1;
            }
            else
            {
                int end = value.IndexOf(',', i);
                if (end < 0) end = value.Length;
                item = value[i..end].Trim();
                i = end;
            }
            if (key.Length > 0) map[key] = item;
        }
        return map;
    }

    bool UserInfoOk(string url)
    {
        int scheme = url.IndexOf("://", StringComparison.Ordinal);
        if (scheme < 0) return false;
        int start = scheme + 3;
        int at = url.IndexOf('@', start);
        if (at < 0) return false;
        int slash = url.IndexOf('/', start);
        if (slash >= 0 && slash < at) return false;
        var info = url[start..at];
        int colon = info.IndexOf(':');
        string user = Uri.UnescapeDataString(colon < 0 ? info : info[..colon]);
        string pass = colon < 0 ? "" : Uri.UnescapeDataString(info[(colon + 1)..]);
        return Credentials(user, pass);
    }

    bool Credentials(string user, string pass) =>
        FixedEquals(user, _config.Username) && FixedEquals(pass, _config.Password);

    static string Md5Hex(string text)
    {
        var hash = MD5.HashData(Encoding.UTF8.GetBytes(text));
        return Convert.ToHexString(hash).ToLowerInvariant();
    }

    static bool HexEquals(string a, string b) =>
        a.Length == b.Length && a.Equals(b, StringComparison.OrdinalIgnoreCase);

    static string Shown(string value)
    {
        var chars = value.Where(c => c >= ' ' && c < 127).Take(64).ToArray();
        return new string(chars);
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
        client.LastStatus = code;
        var head = $"RTSP/1.0 {code} {reason}\r\nCSeq: {cseq}\r\nServer: WebcamStream\r\n{extra}\r\n";
        var bytes = Encoding.ASCII.GetBytes(head);
        lock (client.WriteLock)
        {
            var stream = client.Tcp.GetStream();
            stream.Write(bytes, 0, bytes.Length);
            if (body != null && body.Length > 0)
                stream.Write(body, 0, body.Length);
            stream.Flush();
        }
        if (code != 401)
            Log.Info($"Answered {code} {reason} to cseq {cseq} from {client.Remote.Address}:{client.Remote.Port}.");
    }

    static List<string> Unfold(string text)
    {
        var raw = text.Replace("\r\n", "\n").Replace('\r', '\n').Split('\n');
        var lines = new List<string>();
        foreach (var piece in raw)
        {
            if (piece.Length > 0 && (piece[0] == ' ' || piece[0] == '\t') && lines.Count > 0)
                lines[^1] += " " + piece.Trim();
            else
                lines.Add(piece);
        }
        return lines;
    }

    static string AuthNote(Dictionary<string, string> headers, string url)
    {
        if (headers.TryGetValue("Authorization", out var value))
        {
            if (value.StartsWith("Basic ", StringComparison.OrdinalIgnoreCase))
            {
                try
                {
                    string decoded = Encoding.UTF8.GetString(Convert.FromBase64String(value["Basic ".Length..].Trim()));
                    int colon = decoded.IndexOf(':');
                    string user = colon < 0 ? decoded : decoded[..colon];
                    return $"basic user \"{Shown(user.Trim())}\"";
                }
                catch
                {
                    return "basic login could not be read";
                }
            }
            if (value.StartsWith("Digest ", StringComparison.OrdinalIgnoreCase))
            {
                var fields = ParseDigest(value["Digest ".Length..]);
                fields.TryGetValue("username", out var user);
                string qop = fields.ContainsKey("qop") ? "with qop" : "without qop";
                return $"digest user \"{Shown(user ?? "")}\", {qop}";
            }
            return "unrecognized login";
        }
        if (UrlHasUserInfo(url)) return "username in the URL";
        return "no login";
    }

    static bool UrlHasUserInfo(string url)
    {
        int scheme = url.IndexOf("://", StringComparison.Ordinal);
        if (scheme < 0) return false;
        int start = scheme + 3;
        int at = url.IndexOf('@', start);
        if (at < 0) return false;
        int slash = url.IndexOf('/', start);
        return slash < 0 || slash > at;
    }

    static string Redact(string url)
    {
        int scheme = url.IndexOf("://", StringComparison.Ordinal);
        if (scheme < 0) return url;
        int start = scheme + 3;
        int at = url.IndexOf('@', start);
        if (at < 0) return url;
        int slash = url.IndexOf('/', start);
        if (slash >= 0 && slash < at) return url;
        return url[..start] + url[(at + 1)..];
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
            bool crlf = n >= 4 && buf[n - 4] == '\r' && buf[n - 3] == '\n' && buf[n - 2] == '\r' && buf[n - 1] == '\n';
            bool lf = n >= 2 && buf[n - 1] == '\n' && buf[n - 2] == '\n';
            if (crlf || lf)
                return Encoding.ASCII.GetString(buf.ToArray(), 0, n - (crlf ? 4 : 2));
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

    static string Farewell(Client client)
    {
        if (client.Requests == 0)
            return client.QuietTimeout
                ? "It connected and sent no RTSP request before the connection was closed."
                : "It connected and sent no RTSP request.";
        string last = $"The last was {client.LastMethod} answered {client.LastStatus}.";
        string count = client.Requests == 1 ? "1 request" : $"{client.Requests} requests";
        if (client.QuietTimeout && !client.Playing)
            return $"It sent {count} and then sat quiet, so the connection was closed. {last} It never started playing.";
        if (client.LastStatus == 401 && !client.SentCredentials)
            return $"It sent {count}. {last} No username or password was sent.";
        if (client.LastStatus == 401)
            return $"It sent {count}. {last} The login was rejected.";
        if (!client.Playing)
            return $"It sent {count}. {last} It never started playing.";
        return $"It sent {count}. {last}";
    }

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
            Log.Info($"Client disconnected {client.Remote.Address}:{client.Remote.Port}. {Farewell(client)}");
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
        public string? PresentationUrl { get; set; }
        public volatile bool Playing;
        public bool Paused;
        public volatile bool Closed;
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
        public int Requests;
        public string LastMethod = "";
        public int LastStatus;
        public bool SentCredentials;
        public bool QuietTimeout;

        public void Touch() => LastActivity = DateTime.UtcNow;
    }
}
