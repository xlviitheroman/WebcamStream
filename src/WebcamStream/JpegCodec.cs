namespace WebcamStream;

sealed class EncodedJpeg
{
    public int Width { get; init; }
    public int Height { get; init; }
    public int Type { get; init; }
    public int RestartInterval { get; init; }
    public byte[] LumaQuant { get; init; } = Array.Empty<byte>();
    public byte[] ChromaQuant { get; init; } = Array.Empty<byte>();
    public byte[] Scan { get; init; } = Array.Empty<byte>();
    public byte[] Container { get; init; } = Array.Empty<byte>();
    public bool StandardHuffman { get; init; } = true;
    public bool Supported { get; init; } = true;

    public bool RecorderCanPlay =>
        Supported
        && StandardHuffman
        && RestartInterval is >= 0 and <= 65535
        && Scan.Length > 0
        && Width >= 16
        && Height >= 16
        && LumaQuant.Length == 64
        && ChromaQuant.Length == 64
        && Type switch
        {
            0 => (Width & 15) == 0 && (Height & 7) == 0,
            1 => (Width & 15) == 0 && (Height & 15) == 0,
            _ => false,
        };
}

static class JpegCodec
{
    static readonly int[] ZigZag =
    {
        0, 1, 8, 16, 9, 2, 3, 10,
        17, 24, 32, 25, 18, 11, 4, 5,
        12, 19, 26, 33, 40, 48, 41, 34,
        27, 20, 13, 6, 7, 14, 21, 28,
        35, 42, 49, 56, 57, 50, 43, 36,
        29, 22, 15, 23, 30, 37, 44, 51,
        58, 59, 52, 45, 38, 31, 39, 46,
        53, 60, 61, 54, 47, 55, 62, 63,
    };

    static readonly int[] LumaBase =
    {
        16, 11, 10, 16, 24, 40, 51, 61,
        12, 12, 14, 19, 26, 58, 60, 55,
        14, 13, 16, 24, 40, 57, 69, 56,
        14, 17, 22, 29, 51, 87, 80, 62,
        18, 22, 37, 56, 68, 109, 103, 77,
        24, 35, 55, 64, 81, 104, 113, 92,
        49, 64, 78, 87, 103, 121, 120, 101,
        72, 92, 95, 98, 112, 100, 103, 99,
    };

    static readonly int[] ChromaBase =
    {
        17, 18, 24, 47, 99, 99, 99, 99,
        18, 21, 26, 66, 99, 99, 99, 99,
        24, 26, 56, 99, 99, 99, 99, 99,
        47, 66, 99, 99, 99, 99, 99, 99,
        99, 99, 99, 99, 99, 99, 99, 99,
        99, 99, 99, 99, 99, 99, 99, 99,
        99, 99, 99, 99, 99, 99, 99, 99,
        99, 99, 99, 99, 99, 99, 99, 99,
    };

    static readonly byte[] DcLumaBits = { 0, 1, 5, 1, 1, 1, 1, 1, 1, 0, 0, 0, 0, 0, 0, 0 };
    static readonly byte[] DcChromaBits = { 0, 3, 1, 1, 1, 1, 1, 1, 1, 1, 1, 0, 0, 0, 0, 0 };
    static readonly byte[] DcSymbols = { 0, 1, 2, 3, 4, 5, 6, 7, 8, 9, 10, 11 };
    static readonly byte[] AcLumaBits = { 0, 2, 1, 3, 3, 2, 4, 3, 5, 5, 4, 4, 0, 0, 1, 0x7d };
    static readonly byte[] AcChromaBits = { 0, 2, 1, 2, 4, 4, 3, 4, 7, 5, 4, 4, 0, 1, 2, 0x77 };
    static readonly byte[] AcLumaSymbols =
    {
        0x01, 0x02, 0x03, 0x00, 0x04, 0x11, 0x05, 0x12,
        0x21, 0x31, 0x41, 0x06, 0x13, 0x51, 0x61, 0x07,
        0x22, 0x71, 0x14, 0x32, 0x81, 0x91, 0xa1, 0x08,
        0x23, 0x42, 0xb1, 0xc1, 0x15, 0x52, 0xd1, 0xf0,
        0x24, 0x33, 0x62, 0x72, 0x82, 0x09, 0x0a, 0x16,
        0x17, 0x18, 0x19, 0x1a, 0x25, 0x26, 0x27, 0x28,
        0x29, 0x2a, 0x34, 0x35, 0x36, 0x37, 0x38, 0x39,
        0x3a, 0x43, 0x44, 0x45, 0x46, 0x47, 0x48, 0x49,
        0x4a, 0x53, 0x54, 0x55, 0x56, 0x57, 0x58, 0x59,
        0x5a, 0x63, 0x64, 0x65, 0x66, 0x67, 0x68, 0x69,
        0x6a, 0x73, 0x74, 0x75, 0x76, 0x77, 0x78, 0x79,
        0x7a, 0x83, 0x84, 0x85, 0x86, 0x87, 0x88, 0x89,
        0x8a, 0x92, 0x93, 0x94, 0x95, 0x96, 0x97, 0x98,
        0x99, 0x9a, 0xa2, 0xa3, 0xa4, 0xa5, 0xa6, 0xa7,
        0xa8, 0xa9, 0xaa, 0xb2, 0xb3, 0xb4, 0xb5, 0xb6,
        0xb7, 0xb8, 0xb9, 0xba, 0xc2, 0xc3, 0xc4, 0xc5,
        0xc6, 0xc7, 0xc8, 0xc9, 0xca, 0xd2, 0xd3, 0xd4,
        0xd5, 0xd6, 0xd7, 0xd8, 0xd9, 0xda, 0xe1, 0xe2,
        0xe3, 0xe4, 0xe5, 0xe6, 0xe7, 0xe8, 0xe9, 0xea,
        0xf1, 0xf2, 0xf3, 0xf4, 0xf5, 0xf6, 0xf7, 0xf8,
        0xf9, 0xfa,
    };
    static readonly byte[] AcChromaSymbols =
    {
        0x00, 0x01, 0x02, 0x03, 0x11, 0x04, 0x05, 0x21,
        0x31, 0x06, 0x12, 0x41, 0x51, 0x07, 0x61, 0x71,
        0x13, 0x22, 0x32, 0x81, 0x08, 0x14, 0x42, 0x91,
        0xa1, 0xb1, 0xc1, 0x09, 0x23, 0x33, 0x52, 0xf0,
        0x15, 0x62, 0x72, 0xd1, 0x0a, 0x16, 0x24, 0x34,
        0xe1, 0x25, 0xf1, 0x17, 0x18, 0x19, 0x1a, 0x26,
        0x27, 0x28, 0x29, 0x2a, 0x35, 0x36, 0x37, 0x38,
        0x39, 0x3a, 0x43, 0x44, 0x45, 0x46, 0x47, 0x48,
        0x49, 0x4a, 0x53, 0x54, 0x55, 0x56, 0x57, 0x58,
        0x59, 0x5a, 0x63, 0x64, 0x65, 0x66, 0x67, 0x68,
        0x69, 0x6a, 0x73, 0x74, 0x75, 0x76, 0x77, 0x78,
        0x79, 0x7a, 0x82, 0x83, 0x84, 0x85, 0x86, 0x87,
        0x88, 0x89, 0x8a, 0x92, 0x93, 0x94, 0x95, 0x96,
        0x97, 0x98, 0x99, 0x9a, 0xa2, 0xa3, 0xa4, 0xa5,
        0xa6, 0xa7, 0xa8, 0xa9, 0xaa, 0xb2, 0xb3, 0xb4,
        0xb5, 0xb6, 0xb7, 0xb8, 0xb9, 0xba, 0xc2, 0xc3,
        0xc4, 0xc5, 0xc6, 0xc7, 0xc8, 0xc9, 0xca, 0xd2,
        0xd3, 0xd4, 0xd5, 0xd6, 0xd7, 0xd8, 0xd9, 0xda,
        0xe2, 0xe3, 0xe4, 0xe5, 0xe6, 0xe7, 0xe8, 0xe9,
        0xea, 0xf2, 0xf3, 0xf4, 0xf5, 0xf6, 0xf7, 0xf8,
        0xf9, 0xfa,
    };

    static readonly float[,] DctWeight = new float[8, 8];
    static readonly ushort[] DcLumaCode = new ushort[16];
    static readonly byte[] DcLumaLen = new byte[16];
    static readonly ushort[] DcChromaCode = new ushort[16];
    static readonly byte[] DcChromaLen = new byte[16];
    static readonly ushort[] AcLumaCode = new ushort[256];
    static readonly byte[] AcLumaLen = new byte[256];
    static readonly ushort[] AcChromaCode = new ushort[256];
    static readonly byte[] AcChromaLen = new byte[256];

    static JpegCodec()
    {
        Build(DcLumaBits, DcSymbols, DcLumaCode, DcLumaLen);
        Build(DcChromaBits, DcSymbols, DcChromaCode, DcChromaLen);
        Build(AcLumaBits, AcLumaSymbols, AcLumaCode, AcLumaLen);
        Build(AcChromaBits, AcChromaSymbols, AcChromaCode, AcChromaLen);
        for (int k = 0; k < 8; k++)
        {
            double alpha = k == 0 ? 1.0 / Math.Sqrt(2) : 1.0;
            for (int n = 0; n < 8; n++)
                DctWeight[k, n] = (float)(0.5 * alpha * Math.Cos((2 * n + 1) * k * Math.PI / 16.0));
        }
    }

    public static EncodedJpeg Encode420(ReadOnlySpan<byte> y, ReadOnlySpan<byte> cb, ReadOnlySpan<byte> cr, int width, int height, int quality)
    {
        if ((width & 15) != 0 || (height & 15) != 0)
            throw new ArgumentException("Software JPEG needs width and height that are multiples of 16.");
        if (y.Length < width * height || cb.Length < (width / 2) * (height / 2) || cr.Length < (width / 2) * (height / 2))
            throw new ArgumentException("Color planes are smaller than the picture.");

        quality = Math.Clamp(quality, 1, 95);
        var lumaNatural = Scale(LumaBase, quality);
        var chromaNatural = Scale(ChromaBase, quality);
        var lumaZig = Zig(lumaNatural);
        var chromaZig = Zig(chromaNatural);

        var bits = new BitWriter(width * height);
        int predY = 0, predCb = 0, predCr = 0;
        int chromaStride = width / 2;
        for (int my = 0; my < height; my += 16)
        {
            for (int mx = 0; mx < width; mx += 16)
            {
                EncodeBlock(bits, y, width, mx, my, lumaNatural, DcLumaCode, DcLumaLen, AcLumaCode, AcLumaLen, ref predY);
                EncodeBlock(bits, y, width, mx + 8, my, lumaNatural, DcLumaCode, DcLumaLen, AcLumaCode, AcLumaLen, ref predY);
                EncodeBlock(bits, y, width, mx, my + 8, lumaNatural, DcLumaCode, DcLumaLen, AcLumaCode, AcLumaLen, ref predY);
                EncodeBlock(bits, y, width, mx + 8, my + 8, lumaNatural, DcLumaCode, DcLumaLen, AcLumaCode, AcLumaLen, ref predY);
                EncodeBlock(bits, cb, chromaStride, mx / 2, my / 2, chromaNatural, DcChromaCode, DcChromaLen, AcChromaCode, AcChromaLen, ref predCb);
                EncodeBlock(bits, cr, chromaStride, mx / 2, my / 2, chromaNatural, DcChromaCode, DcChromaLen, AcChromaCode, AcChromaLen, ref predCr);
            }
        }
        var scan = bits.Finish();
        var container = BuildContainer(width, height, 1, 0, lumaZig, chromaZig, scan);
        return new EncodedJpeg
        {
            Width = width,
            Height = height,
            Type = 1,
            RestartInterval = 0,
            LumaQuant = lumaZig,
            ChromaQuant = chromaZig,
            Scan = scan,
            Container = container,
            StandardHuffman = true,
            Supported = true,
        };
    }

    public static EncodedJpeg? TryParse(ReadOnlySpan<byte> jpeg)
    {
        if (jpeg.Length < 4 || jpeg[0] != 0xFF || jpeg[1] != 0xD8)
            return null;

        int width = 0, height = 0, type = -1, restart = 0;
        byte[]? luma = null, chroma = null;
        bool standardHuffman = true;
        int i = 2;
        while (i + 1 < jpeg.Length)
        {
            if (jpeg[i] != 0xFF)
                return null;
            while (i < jpeg.Length && jpeg[i] == 0xFF)
                i++;
            if (i >= jpeg.Length)
                return null;
            int marker = jpeg[i++];
            if (marker == 0xD9)
                return null;
            if (marker == 0xDA)
            {
                if (i + 1 >= jpeg.Length)
                    return null;
                int sosLen = (jpeg[i] << 8) | jpeg[i + 1];
                int scanStart = i + sosLen;
                if (scanStart > jpeg.Length || width == 0 || luma == null || type < 0)
                    return null;
                // One quantization table shared by every component is valid JPEG.
                // RTP still needs a chroma table, so reuse the luma table.
                chroma ??= (byte[])luma.Clone();
                var scan = ExtractScan(jpeg[scanStart..]);
                return new EncodedJpeg
                {
                    Width = width,
                    Height = height,
                    Type = type,
                    RestartInterval = restart,
                    LumaQuant = luma,
                    ChromaQuant = chroma,
                    Scan = scan,
                    Container = jpeg.ToArray(),
                    StandardHuffman = standardHuffman,
                    Supported = true,
                };
            }
            if (i + 1 >= jpeg.Length)
                return null;
            int len = (jpeg[i] << 8) | jpeg[i + 1];
            if (len < 2 || i + len > jpeg.Length)
                return null;
            var body = jpeg.Slice(i + 2, len - 2);
            if (marker == 0xC0)
            {
                if (body.Length < 6)
                    return null;
                height = (body[1] << 8) | body[2];
                width = (body[3] << 8) | body[4];
                int components = body[5];
                if (components < 3 || body.Length < 6 + components * 3)
                    return null;
                int sampling = body[7];
                type = sampling switch
                {
                    0x22 => 1,
                    0x21 => 0,
                    _ => -2,
                };
                if (body[10] != 0x11 || body[13] != 0x11)
                    type = -2;
            }
            else if (marker == 0xC2)
            {
                return null;
            }
            else if (marker == 0xDB)
            {
                int p = 0;
                while (p < body.Length)
                {
                    int info = body[p++];
                    bool sixteen = (info & 0xF0) != 0;
                    int id = info & 0x0F;
                    int size = sixteen ? 128 : 64;
                    if (p + size > body.Length)
                        return null;
                    if (!sixteen && id == 0)
                        luma = body.Slice(p, 64).ToArray();
                    else if (!sixteen && id == 1)
                        chroma = body.Slice(p, 64).ToArray();
                    p += size;
                }
            }
            else if (marker == 0xDD)
            {
                if (body.Length >= 2)
                    restart = (body[0] << 8) | body[1];
            }
            else if (marker == 0xC4)
            {
                if (!HuffmanMatchesStandard(body))
                    standardHuffman = false;
            }
            if (type == -2)
                return new EncodedJpeg { Supported = false, StandardHuffman = standardHuffman };
            i += len;
        }
        return null;
    }

    public static byte[] BuildContainer(int width, int height, int type, int restart, byte[] lumaZig, byte[] chromaZig, byte[] scan)
    {
        int ySampling = type == 0 ? 0x21 : 0x22;
        using var ms = new MemoryStream(scan.Length + 700);
        void Mark(byte code) { ms.WriteByte(0xFF); ms.WriteByte(code); }
        void Be16(int v) { ms.WriteByte((byte)(v >> 8)); ms.WriteByte((byte)v); }

        Mark(0xD8);
        Mark(0xE0);
        Be16(16);
        ms.Write(new byte[] { 0x4A, 0x46, 0x49, 0x46, 0x00, 0x01, 0x01, 0x00, 0x00, 0x01, 0x00, 0x01, 0x00, 0x00 });
        WriteDqt(ms, 0, lumaZig);
        WriteDqt(ms, 1, chromaZig);
        Mark(0xC0);
        Be16(17);
        ms.WriteByte(8);
        Be16(height);
        Be16(width);
        ms.WriteByte(3);
        ms.WriteByte(1); ms.WriteByte((byte)ySampling); ms.WriteByte(0);
        ms.WriteByte(2); ms.WriteByte(0x11); ms.WriteByte(1);
        ms.WriteByte(3); ms.WriteByte(0x11); ms.WriteByte(1);
        if (restart > 0)
        {
            Mark(0xDD);
            Be16(4);
            Be16(restart);
        }
        WriteDht(ms);
        Mark(0xDA);
        Be16(12);
        ms.WriteByte(3);
        ms.WriteByte(1); ms.WriteByte(0x00);
        ms.WriteByte(2); ms.WriteByte(0x11);
        ms.WriteByte(3); ms.WriteByte(0x11);
        ms.WriteByte(0); ms.WriteByte(63); ms.WriteByte(0);
        ms.Write(scan);
        Mark(0xD9);
        return ms.ToArray();
    }

    static void WriteDqt(Stream ms, int id, byte[] zig)
    {
        ms.WriteByte(0xFF); ms.WriteByte(0xDB);
        ms.WriteByte(0); ms.WriteByte(67);
        ms.WriteByte((byte)id);
        ms.Write(zig);
    }

    static void WriteDht(Stream ms)
    {
        int payload = 2 + (1 + 16 + DcSymbols.Length) * 2 + (1 + 16 + AcLumaSymbols.Length) * 2;
        ms.WriteByte(0xFF); ms.WriteByte(0xC4);
        ms.WriteByte((byte)(payload >> 8));
        ms.WriteByte((byte)payload);
        WriteOneTable(ms, 0x00, DcLumaBits, DcSymbols);
        WriteOneTable(ms, 0x10, AcLumaBits, AcLumaSymbols);
        WriteOneTable(ms, 0x01, DcChromaBits, DcSymbols);
        WriteOneTable(ms, 0x11, AcChromaBits, AcChromaSymbols);
    }

    static void WriteOneTable(Stream ms, byte cls, byte[] bits, byte[] symbols)
    {
        ms.WriteByte(cls);
        ms.Write(bits);
        ms.Write(symbols);
    }

    static bool HuffmanMatchesStandard(ReadOnlySpan<byte> body)
    {
        int p = 0;
        while (p < body.Length)
        {
            if (p + 17 > body.Length)
                return false;
            int cls = body[p++];
            int count = 0;
            for (int i = 0; i < 16; i++)
                count += body[p++];
            if (p + count > body.Length)
                return false;
            var bits = body.Slice(p - 16, 16);
            var symbols = body.Slice(p, count);
            p += count;
            byte[] expectBits;
            byte[] expectSymbols;
            switch (cls)
            {
                case 0x00: expectBits = DcLumaBits; expectSymbols = DcSymbols; break;
                case 0x01: expectBits = DcChromaBits; expectSymbols = DcSymbols; break;
                case 0x10: expectBits = AcLumaBits; expectSymbols = AcLumaSymbols; break;
                case 0x11: expectBits = AcChromaBits; expectSymbols = AcChromaSymbols; break;
                default: return false;
            }
            if (!bits.SequenceEqual(expectBits) || !symbols.SequenceEqual(expectSymbols))
                return false;
        }
        return true;
    }

    static byte[] ExtractScan(ReadOnlySpan<byte> data)
    {
        var buf = new List<byte>(data.Length);
        for (int i = 0; i < data.Length; i++)
        {
            byte b = data[i];
            if (b != 0xFF)
            {
                buf.Add(b);
                continue;
            }
            if (i + 1 >= data.Length)
                break;
            byte n = data[++i];
            if (n == 0x00)
            {
                buf.Add(0xFF);
                buf.Add(0x00);
            }
            else if (n >= 0xD0 && n <= 0xD7)
            {
                buf.Add(0xFF);
                buf.Add(n);
            }
            else if (n == 0xD9)
            {
                break;
            }
            else if (n == 0xFF)
            {
                i--;
            }
            else
            {
                break;
            }
        }
        return buf.ToArray();
    }

    static int[] Scale(int[] table, int quality)
    {
        int factor = quality < 50 ? 5000 / quality : 200 - quality * 2;
        var scaled = new int[64];
        for (int i = 0; i < 64; i++)
        {
            int v = (table[i] * factor + 50) / 100;
            if (v < 1) v = 1;
            if (v > 255) v = 255;
            scaled[i] = v;
        }
        return scaled;
    }

    static byte[] Zig(int[] natural)
    {
        var zig = new byte[64];
        for (int i = 0; i < 64; i++)
            zig[i] = (byte)natural[ZigZag[i]];
        return zig;
    }

    static void EncodeBlock(
        BitWriter bits, ReadOnlySpan<byte> plane, int stride, int ox, int oy, int[] quant,
        ushort[] dcCode, byte[] dcLen, ushort[] acCode, byte[] acLen, ref int pred)
    {
        Span<float> block = stackalloc float[64];
        Span<float> freq = stackalloc float[64];
        int i = 0;
        for (int y = 0; y < 8; y++)
        {
            int row = (oy + y) * stride + ox;
            for (int x = 0; x < 8; x++)
                block[i++] = plane[row + x] - 128f;
        }
        Dct(block, freq);
        Span<int> zz = stackalloc int[64];
        for (int n = 0; n < 64; n++)
        {
            int natural = ZigZag[n];
            float q = freq[natural] / quant[natural];
            zz[n] = (int)Math.Round(q, MidpointRounding.AwayFromZero);
        }
        int diff = zz[0] - pred;
        pred = zz[0];
        WriteDc(bits, dcCode, dcLen, diff);
        int run = 0;
        for (int n = 1; n < 64; n++)
        {
            int ac = zz[n];
            if (ac == 0)
            {
                run++;
                continue;
            }
            while (run > 15)
            {
                bits.Write(acCode[0xF0], acLen[0xF0]);
                run -= 16;
            }
            WriteAc(bits, acCode, acLen, run, ac);
            run = 0;
        }
        if (run > 0)
            bits.Write(acCode[0x00], acLen[0x00]);
    }

    static void Dct(ReadOnlySpan<float> src, Span<float> dst)
    {
        Span<float> tmp = stackalloc float[64];
        for (int y = 0; y < 8; y++)
        {
            for (int u = 0; u < 8; u++)
            {
                float s = 0;
                for (int x = 0; x < 8; x++)
                    s += src[y * 8 + x] * DctWeight[u, x];
                tmp[y * 8 + u] = s;
            }
        }
        for (int u = 0; u < 8; u++)
        {
            for (int v = 0; v < 8; v++)
            {
                float s = 0;
                for (int y = 0; y < 8; y++)
                    s += tmp[y * 8 + u] * DctWeight[v, y];
                dst[v * 8 + u] = s;
            }
        }
    }

    static void WriteDc(BitWriter bits, ushort[] code, byte[] len, int diff)
    {
        int cat = Category(diff);
        if (cat > 11)
        {
            diff = diff < 0 ? -2047 : 2047;
            cat = 11;
        }
        bits.Write(code[cat], len[cat]);
        if (cat > 0)
            bits.Write(diff < 0 ? diff - 1 : diff, cat);
    }

    static void WriteAc(BitWriter bits, ushort[] code, byte[] len, int run, int value)
    {
        int cat = Category(value);
        if (cat > 10)
        {
            value = value < 0 ? -1023 : 1023;
            cat = 10;
        }
        int symbol = (run << 4) | cat;
        bits.Write(code[symbol], len[symbol]);
        if (cat > 0)
            bits.Write(value < 0 ? value - 1 : value, cat);
    }

    static int Category(int value)
    {
        if (value < 0) value = -value;
        int cat = 0;
        while (value > 0)
        {
            value >>= 1;
            cat++;
        }
        return cat;
    }

    static void Build(byte[] bits, byte[] symbols, ushort[] codes, byte[] lens)
    {
        int sum = 0;
        foreach (var b in bits) sum += b;
        if (sum != symbols.Length)
            throw new InvalidOperationException("Huffman table is inconsistent.");
        int code = 0;
        int k = 0;
        for (int len = 1; len <= 16; len++)
        {
            int count = bits[len - 1];
            for (int i = 0; i < count; i++)
            {
                int sym = symbols[k++];
                codes[sym] = (ushort)code;
                lens[sym] = (byte)len;
                code++;
            }
            code <<= 1;
        }
    }

    sealed class BitWriter
    {
        byte[] _buf;
        int _len;
        uint _acc;
        int _n;

        public BitWriter(int cap) => _buf = new byte[Math.Max(64, cap / 8)];

        public void Write(int code, int nbits)
        {
            if (nbits <= 0) return;
            _acc = (_acc << nbits) | (uint)(code & ((1 << nbits) - 1));
            _n += nbits;
            while (_n >= 8)
            {
                _n -= 8;
                Push((int)((_acc >> _n) & 0xFF));
            }
        }

        public byte[] Finish()
        {
            if (_n > 0)
            {
                int pad = 8 - _n;
                _acc = (_acc << pad) | (uint)((1 << pad) - 1);
                _n += pad;
                while (_n >= 8)
                {
                    _n -= 8;
                    Push((int)((_acc >> _n) & 0xFF));
                }
            }
            var result = new byte[_len];
            Buffer.BlockCopy(_buf, 0, result, 0, _len);
            return result;
        }

        void Push(int b)
        {
            if (_len + 2 > _buf.Length)
                Array.Resize(ref _buf, _buf.Length * 2);
            _buf[_len++] = (byte)b;
            if (b == 0xFF)
                _buf[_len++] = 0x00;
        }
    }
}
