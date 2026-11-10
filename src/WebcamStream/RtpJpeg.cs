namespace WebcamStream;

readonly struct RtpSlice
{
    public RtpSlice(byte[] payload, bool marker)
    {
        Payload = payload;
        Marker = marker;
    }

    public byte[] Payload { get; }
    public bool Marker { get; }
}

sealed class VideoFrame
{
    public int Id { get; init; }
    public int Width { get; init; }
    public int Height { get; init; }
    public RtpSlice[] Slices { get; init; } = Array.Empty<RtpSlice>();
    public string Mode { get; init; } = "";
}

static class RtpJpeg
{
    public const int PayloadType = 26;
    public const int Clock = 90000;

    public static VideoFrame Packetize(EncodedJpeg jpeg, int frameId, string mode, int maxPayload = 1400)
    {
        if (jpeg.Width <= 0 || jpeg.Height <= 0 || jpeg.Width > 2040 || jpeg.Height > 2040)
            throw new ArgumentException($"RTP JPEG cannot describe {jpeg.Width}x{jpeg.Height}. The limit is 2040 on each side.");
        if ((jpeg.Width & 7) != 0 || (jpeg.Height & 7) != 0)
            throw new ArgumentException("RTP JPEG width and height must be multiples of 8.");

        int type = jpeg.Type;
        if (jpeg.RestartInterval > 0)
            type += 64;
        var slices = new List<RtpSlice>();
        int offset = 0;
        var scan = jpeg.Scan;
        while (offset < scan.Length || slices.Count == 0)
        {
            int header = 8;
            if (jpeg.RestartInterval > 0)
                header += 4;
            bool first = offset == 0;
            if (first)
                header += 4 + 128;
            int room = Math.Max(1, maxPayload - header);
            int take = Math.Min(room, Math.Max(0, scan.Length - offset));
            var payload = new byte[header + take];
            int p = 0;
            payload[p++] = 0;
            payload[p++] = (byte)((offset >> 16) & 0xFF);
            payload[p++] = (byte)((offset >> 8) & 0xFF);
            payload[p++] = (byte)(offset & 0xFF);
            payload[p++] = (byte)type;
            payload[p++] = 255;
            payload[p++] = (byte)(jpeg.Width / 8);
            payload[p++] = (byte)(jpeg.Height / 8);
            if (jpeg.RestartInterval > 0)
            {
                payload[p++] = (byte)((jpeg.RestartInterval >> 8) & 0xFF);
                payload[p++] = (byte)(jpeg.RestartInterval & 0xFF);
                // F = L = 1 and count 0x3FFF tells the receiver to reassemble
                // the whole frame before decoding. That is the safe choice when
                // restart markers are not aligned to packets.
                payload[p++] = 0xFF;
                payload[p++] = 0xFF;
            }
            if (first)
            {
                payload[p++] = 0;
                payload[p++] = 0;
                payload[p++] = 0;
                payload[p++] = 128;
                Buffer.BlockCopy(jpeg.LumaQuant, 0, payload, p, 64);
                p += 64;
                Buffer.BlockCopy(jpeg.ChromaQuant, 0, payload, p, 64);
                p += 64;
            }
            if (take > 0)
                Buffer.BlockCopy(scan, offset, payload, p, take);
            offset += take;
            bool last = offset >= scan.Length;
            slices.Add(new RtpSlice(payload, last));
            if (last)
                break;
        }
        return new VideoFrame
        {
            Id = frameId,
            Width = jpeg.Width,
            Height = jpeg.Height,
            Slices = slices.ToArray(),
            Mode = mode,
        };
    }

    public static byte[] WriteHeader(int sequence, uint timestamp, bool marker, uint ssrc, int payloadLength)
    {
        var packet = new byte[12 + payloadLength];
        packet[0] = 0x80;
        packet[1] = (byte)((marker ? 0x80 : 0) | PayloadType);
        packet[2] = (byte)((sequence >> 8) & 0xFF);
        packet[3] = (byte)(sequence & 0xFF);
        packet[4] = (byte)((timestamp >> 24) & 0xFF);
        packet[5] = (byte)((timestamp >> 16) & 0xFF);
        packet[6] = (byte)((timestamp >> 8) & 0xFF);
        packet[7] = (byte)(timestamp & 0xFF);
        packet[8] = (byte)((ssrc >> 24) & 0xFF);
        packet[9] = (byte)((ssrc >> 16) & 0xFF);
        packet[10] = (byte)((ssrc >> 8) & 0xFF);
        packet[11] = (byte)(ssrc & 0xFF);
        return packet;
    }

    public static byte[] SenderReport(uint ssrc, uint rtpTimestamp, uint packetCount, uint octetCount)
    {
        var ntp = DateTime.UtcNow - new DateTime(1900, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        uint seconds = (uint)ntp.TotalSeconds;
        uint fraction = (uint)((ntp.TotalSeconds - seconds) * uint.MaxValue);
        var buf = new byte[28];
        buf[0] = 0x80;
        buf[1] = 200;
        buf[2] = 0;
        buf[3] = 6;
        WriteU32(buf, 4, ssrc);
        WriteU32(buf, 8, seconds);
        WriteU32(buf, 12, fraction);
        WriteU32(buf, 16, rtpTimestamp);
        WriteU32(buf, 20, packetCount);
        WriteU32(buf, 24, octetCount);
        return buf;
    }

    static void WriteU32(byte[] buf, int at, uint value)
    {
        buf[at] = (byte)(value >> 24);
        buf[at + 1] = (byte)(value >> 16);
        buf[at + 2] = (byte)(value >> 8);
        buf[at + 3] = (byte)value;
    }
}
