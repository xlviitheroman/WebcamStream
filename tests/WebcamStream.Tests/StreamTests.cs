using System.Diagnostics;
using Xunit;
using System.Net;
using System.Net.Sockets;
using System.Text;
using WebcamStream;

namespace WebcamStream.Tests;

public class StreamTests
{
    [Fact]
    public void DefaultConfigKeepsCommentsAnd720p()
    {
        var path = Path.Combine(Path.GetTempPath(), "webcamstream-config-" + Guid.NewGuid().ToString("n") + ".jsonc");
        try
        {
            AppConfig.WriteDefault(path);
            var text = File.ReadAllText(path);
            Assert.Contains("720p", text);
            Assert.Contains("554", text);
            Assert.Contains("//", text);
            var config = AppConfig.Read(path);
            Assert.Equal(554, config.Port);
            Assert.Equal(1280, config.Width);
            Assert.Equal(720, config.Height);
            Assert.Equal(4, config.Fps);
            Assert.Equal("0.0.0.0", config.ListenAddress);
            Assert.Empty(config.Allow);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void AllowListMatchesAddressSubnetAndName()
    {
        var access = new AccessControl(
            name => name.Equals("exacq.office.example.com", StringComparison.OrdinalIgnoreCase)
                ? new[] { IPAddress.Parse("10.8.0.4") }
                : Array.Empty<IPAddress>(),
            ip => ip.Equals(IPAddress.Parse("10.9.0.8")) ? "nvr.office.example.com" : "other.example.net");
        access.Replace(new[] { "192.168.1.0/24", "10.4.4.4", "exacq.office.example.com", ".office.example.com" });
        Assert.True(access.Permits(IPAddress.Parse("192.168.1.50")));
        Assert.False(access.Permits(IPAddress.Parse("192.168.2.50")));
        Assert.True(access.Permits(IPAddress.Parse("10.4.4.4")));
        Assert.True(access.Permits(IPAddress.Parse("10.8.0.4")));
        Assert.True(access.Permits(IPAddress.Parse("10.9.0.8")));
        Assert.False(access.Permits(IPAddress.Parse("8.8.8.8")));

        var mask = new AccessControl(_ => Array.Empty<IPAddress>(), _ => null);
        mask.Replace(new[] { "10.0.0.0/255.255.0.0" });
        Assert.True(mask.Permits(IPAddress.Parse("10.0.9.9")));
        Assert.False(mask.Permits(IPAddress.Parse("10.1.0.1")));
        Assert.False(mask.Permits(IPAddress.Parse("11.0.0.1")));
    }

    [Fact]
    public void EmptyAllowListPermitsEveryone()
    {
        var access = new AccessControl(_ => Array.Empty<IPAddress>(), _ => null);
        access.Replace(Array.Empty<string>());
        Assert.True(access.AllowAll);
        Assert.True(access.Permits(IPAddress.Parse("1.2.3.4")));
    }

    [Fact]
    public void JpegRoundTripKeepsTheColorAnd720pIsCheap()
    {
        var jpeg = Solid(64, 64, 40, 220, 20, 20);
        var parsed = JpegCodec.TryParse(jpeg.Container);
        Assert.NotNull(parsed);
        Assert.Equal(64, parsed!.Width);
        Assert.Equal(64, parsed.Height);
        Assert.Equal(1, parsed.Type);
        Assert.Equal(jpeg.Scan, parsed.Scan);

        var rgb = Decode(jpeg.Container);
        Assert.Equal(64 * 64 * 3, rgb.Length);
        Assert.InRange(rgb[0], 180, 255);
        Assert.InRange(rgb[1], 0, 60);
        Assert.InRange(rgb[2], 0, 60);

        var clock = Stopwatch.StartNew();
        var big = Solid(1280, 720, 40, 80, 90, 100);
        clock.Stop();
        Assert.Equal(1280, big.Width);
        Assert.Equal(720, big.Height);
        Assert.True(clock.ElapsedMilliseconds < 500, $"720p encode took {clock.ElapsedMilliseconds} ms");
    }

    [Fact]
    public void RtpPacketsReassembleToTheSameScan()
    {
        var jpeg = Solid(320, 240, 40, 20, 40, 180);
        var frame = RtpJpeg.Packetize(jpeg, 1, "test", maxPayload: 500);
        Assert.True(frame.Slices.Length > 1);
        Assert.True(frame.Slices[^1].Marker);
        var scan = Reassemble(frame, out int width, out int height, out var luma, out var chroma, out int type);
        Assert.Equal(jpeg.Scan, scan);
        Assert.Equal(320, width);
        Assert.Equal(240, height);
        Assert.Equal(1, type);
        Assert.Equal(jpeg.LumaQuant, luma);
        var container = JpegCodec.BuildContainer(width, height, type, 0, luma, chroma, scan);
        var rgb = Decode(container);
        Assert.InRange(rgb[2], 140, 255);
    }

    [Fact]
    public async Task RtspClientReceivesAJpegFrame()
    {
        var dir = Path.Combine(Path.GetTempPath(), "webcamstream-" + Guid.NewGuid().ToString("n"));
        Directory.CreateDirectory(dir);
        var configPath = Path.Combine(dir, "config.jsonc");
        await File.WriteAllTextAsync(configPath, """
        {
          "listenAddress": "127.0.0.1",
          "port": 0,
          "path": "/webcam",
          "fps": 10,
          "width": 320,
          "height": 240,
          "jpegQuality": 40,
          "allow": [ "127.0.0.1" ],
          "testPattern": true,
          "writeStatus": false
        }
        """);
        var config = AppConfig.Read(configPath);
        var access = new AccessControl(_ => Array.Empty<IPAddress>(), _ => null);
        access.Replace(config.Allow);
        using var server = new RtspServer(config, access);
        server.Start();
        var jpeg = Solid(320, 240, 40, 240, 240, 240);
        server.Publish(RtpJpeg.Packetize(jpeg, 1, "test"));

        using var tcp = new TcpClient();
        await tcp.ConnectAsync(IPAddress.Loopback, server.Port);
        var stream = tcp.GetStream();
        var options = await Exchange(stream, "OPTIONS rtsp://127.0.0.1/webcam RTSP/1.0\r\nCSeq: 1\r\n\r\n");
        Assert.Contains("200", options);
        var describe = await Exchange(stream, "DESCRIBE rtsp://127.0.0.1/webcam RTSP/1.0\r\nCSeq: 2\r\nAccept: application/sdp\r\n\r\n");
        Assert.Contains("JPEG/90000", describe);
        Assert.Contains("m=video", describe);
        var setup = await Exchange(stream, "SETUP rtsp://127.0.0.1/webcam/track1 RTSP/1.0\r\nCSeq: 3\r\nTransport: RTP/AVP/TCP;unicast;interleaved=0-1\r\n\r\n");
        Assert.Contains("200", setup);
        Assert.Contains("interleaved=0-1", setup);
        await WriteAscii(stream, "PLAY rtsp://127.0.0.1/webcam RTSP/1.0\r\nCSeq: 4\r\nSession: 1\r\n\r\n");

        var packets = await ReadRtpUntilMarker(stream, TimeSpan.FromSeconds(5));
        Assert.NotEmpty(packets);
        var scan = ReassemblePackets(packets, out int width, out int height, out var luma, out var chroma, out int type);
        var container = JpegCodec.BuildContainer(width, height, type, 0, luma, chroma, scan);
        var parsed = JpegCodec.TryParse(container);
        Assert.NotNull(parsed);
        Assert.Equal(320, parsed!.Width);
        Assert.Equal(240, parsed.Height);
    }

    static EncodedJpeg Solid(int width, int height, int quality, byte r, byte g, byte b)
    {
        int yv = Clamp((77 * r + 150 * g + 29 * b) >> 8);
        int cbv = Clamp(((-43 * r - 85 * g + 128 * b) >> 8) + 128);
        int crv = Clamp(((128 * r - 107 * g - 21 * b) >> 8) + 128);
        var y = new byte[width * height];
        var cb = new byte[(width / 2) * (height / 2)];
        var cr = new byte[cb.Length];
        Array.Fill(y, (byte)yv);
        Array.Fill(cb, (byte)cbv);
        Array.Fill(cr, (byte)crv);
        return JpegCodec.Encode420(y, cb, cr, width, height, quality);
    }

    static int Clamp(int v) => v < 0 ? 0 : v > 255 ? 255 : v;

    static byte[] Decode(byte[] jpeg)
    {
        var path = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("n") + ".jpg");
        File.WriteAllBytes(path, jpeg);
        try
        {
            var psi = new ProcessStartInfo("ffmpeg", $"-hide_banner -loglevel error -i \"{path}\" -f rawvideo -pix_fmt rgb24 -")
            {
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
            };
            using var process = Process.Start(psi)!;
            using var mem = new MemoryStream();
            process.StandardOutput.BaseStream.CopyTo(mem);
            process.WaitForExit(15000);
            if (process.ExitCode != 0)
                throw new InvalidOperationException(process.StandardError.ReadToEnd());
            return mem.ToArray();
        }
        finally
        {
            File.Delete(path);
        }
    }

    static byte[] Reassemble(VideoFrame frame, out int width, out int height, out byte[] luma, out byte[] chroma, out int type)
    {
        var packets = frame.Slices.Select((slice, index) =>
        {
            var packet = new byte[12 + slice.Payload.Length];
            Buffer.BlockCopy(slice.Payload, 0, packet, 12, slice.Payload.Length);
            packet[1] = (byte)(slice.Marker ? 0x80 | 26 : 26);
            return packet;
        }).ToArray();
        return ReassemblePackets(packets, out width, out height, out luma, out chroma, out type);
    }

    static byte[] ReassemblePackets(IReadOnlyList<byte[]> packets, out int width, out int height, out byte[] luma, out byte[] chroma, out int type)
    {
        var scan = new MemoryStream();
        luma = chroma = Array.Empty<byte>();
        width = height = type = 0;
        foreach (var packet in packets.OrderBy(OffsetOf))
        {
            int offset = OffsetOf(packet);
            int markerType = packet[16];
            int q = packet[17];
            type = markerType & 63;
            width = packet[18] * 8;
            height = packet[19] * 8;
            int p = 20;
            if ((markerType & 64) != 0)
                p += 4;
            if (offset == 0 && q >= 128)
            {
                int tableLen = (packet[p + 2] << 8) | packet[p + 3];
                luma = packet[(p + 4)..(p + 68)];
                chroma = packet[(p + 68)..(p + 4 + tableLen)];
                p += 4 + tableLen;
            }
            scan.Write(packet, p, packet.Length - p);
        }
        return scan.ToArray();
    }

    static int OffsetOf(byte[] packet) => (packet[13] << 16) | (packet[14] << 8) | packet[15];

    static async Task<string> Exchange(NetworkStream stream, string request)
    {
        await WriteAscii(stream, request);
        return await ReadHeaders(stream);
    }

    static async Task WriteAscii(NetworkStream stream, string text)
    {
        var bytes = Encoding.ASCII.GetBytes(text);
        await stream.WriteAsync(bytes);
    }

    static async Task<string> ReadHeaders(NetworkStream stream)
    {
        var buf = new List<byte>();
        while (buf.Count < 65536)
        {
            int b = stream.ReadByte();
            if (b < 0) break;
            if (b == 0x24 && buf.Count == 0)
            {
                await DiscardInterleaved(stream);
                continue;
            }
            buf.Add((byte)b);
            int n = buf.Count;
            if (n >= 4 && buf[n - 4] == '\r' && buf[n - 3] == '\n' && buf[n - 2] == '\r' && buf[n - 1] == '\n')
            {
                var text = Encoding.ASCII.GetString(buf.ToArray());
                if (text.Contains("Content-Length:", StringComparison.OrdinalIgnoreCase))
                {
                    var line = text.Split("\r\n").First(l => l.StartsWith("Content-Length:", StringComparison.OrdinalIgnoreCase));
                    int len = int.Parse(line.Split(':')[1].Trim());
                    var body = new byte[len];
                    int got = 0;
                    while (got < len)
                    {
                        int r = await stream.ReadAsync(body.AsMemory(got));
                        if (r <= 0) break;
                        got += r;
                    }
                    text += Encoding.ASCII.GetString(body);
                }
                return text;
            }
        }
        return Encoding.ASCII.GetString(buf.ToArray());
    }

    static async Task<List<byte[]>> ReadRtpUntilMarker(NetworkStream stream, TimeSpan timeout)
    {
        var packets = new List<byte[]>();
        var until = DateTime.UtcNow + timeout;
        while (DateTime.UtcNow < until)
        {
            int b = stream.ReadByte();
            if (b < 0) break;
            if (b != 0x24)
            {
                var rest = new List<byte> { (byte)b };
                while (rest.Count < 4 || !(rest[^4] == '\r' && rest[^3] == '\n' && rest[^2] == '\r' && rest[^1] == '\n'))
                {
                    int n = stream.ReadByte();
                    if (n < 0) return packets;
                    rest.Add((byte)n);
                    if (rest.Count > 65536) break;
                }
                continue;
            }
            int channel = stream.ReadByte();
            int hi = stream.ReadByte();
            int lo = stream.ReadByte();
            int len = (hi << 8) | lo;
            var packet = new byte[len];
            int got = 0;
            while (got < len)
            {
                int n = await stream.ReadAsync(packet.AsMemory(got));
                if (n <= 0) return packets;
                got += n;
            }
            if (channel == 0)
            {
                packets.Add(packet);
                if ((packet[1] & 0x80) != 0)
                    return packets;
            }
        }
        return packets;
    }

    static async Task DiscardInterleaved(NetworkStream stream)
    {
        int channel = stream.ReadByte();
        int hi = stream.ReadByte();
        int lo = stream.ReadByte();
        int len = (hi << 8) | lo;
        var sink = new byte[len];
        int got = 0;
        while (got < len)
        {
            int n = await stream.ReadAsync(sink.AsMemory(got));
            if (n <= 0) return;
            got += n;
        }
    }
}
