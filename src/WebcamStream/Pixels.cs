namespace WebcamStream;

static class Pixels
{
    public static EncodedJpeg Pattern(int width, int height, int quality, int tick)
    {
        width &= ~15;
        height &= ~15;
        var y = new byte[width * height];
        var cb = new byte[(width / 2) * (height / 2)];
        var cr = new byte[cb.Length];
        Array.Fill(y, (byte)36);
        Array.Fill(cb, (byte)128);
        Array.Fill(cr, (byte)128);

        int bar = (tick * 12) % Math.Max(1, width - 48);
        for (int row = 0; row < height; row++)
        {
            int at = row * width + bar;
            for (int col = 0; col < 48 && bar + col < width; col++)
                y[at + col] = 230;
        }

        Paint(y, cb, cr, width, 16, 16, 96, 96, 210, 40, 36);
        return JpegCodec.Encode420(y, cb, cr, width, height, quality);
    }

    public static EncodedJpeg EncodeBgra(ReadOnlySpan<byte> src, int width, int height, int stride, int quality)
    {
        int w = width & ~15;
        int h = height & ~15;
        bool bottomUp = stride < 0;
        if (bottomUp) stride = -stride;
        var y = new byte[w * h];
        var cb = new byte[(w / 2) * (h / 2)];
        var cr = new byte[cb.Length];
        var sumsCb = new int[cb.Length];
        var sumsCr = new int[cr.Length];
        for (int row = 0; row < h; row++)
        {
            int srcRow = bottomUp ? height - 1 - row : row;
            int srcAt = srcRow * stride;
            for (int col = 0; col < w; col++)
            {
                int p = srcAt + col * 4;
                Rgb(src[p + 2], src[p + 1], src[p], out byte yy, out byte cbb, out byte crr);
                y[row * w + col] = yy;
                int c = (row / 2) * (w / 2) + (col / 2);
                sumsCb[c] += cbb;
                sumsCr[c] += crr;
            }
        }
        for (int i = 0; i < cb.Length; i++)
        {
            cb[i] = (byte)(sumsCb[i] / 4);
            cr[i] = (byte)(sumsCr[i] / 4);
        }
        return JpegCodec.Encode420(y, cb, cr, w, h, quality);
    }

    public static EncodedJpeg EncodeNv12(ReadOnlySpan<byte> src, int width, int height, int yStride, int uvStride, int quality)
    {
        int w = width & ~15;
        int h = height & ~15;
        var y = new byte[w * h];
        var cb = new byte[(w / 2) * (h / 2)];
        var cr = new byte[cb.Length];
        for (int row = 0; row < h; row++)
            src.Slice(row * yStride, w).CopyTo(y.AsSpan(row * w, w));
        int uv = yStride * height;
        for (int row = 0; row < h / 2; row++)
        {
            int s = uv + row * uvStride;
            int d = row * (w / 2);
            for (int col = 0; col < w / 2; col++)
            {
                cb[d + col] = src[s + col * 2];
                cr[d + col] = src[s + col * 2 + 1];
            }
        }
        return JpegCodec.Encode420(y, cb, cr, w, h, quality);
    }

    public static EncodedJpeg EncodeYuy2(ReadOnlySpan<byte> src, int width, int height, int stride, int quality)
    {
        int w = width & ~15;
        int h = height & ~15;
        var y = new byte[w * h];
        var cb = new byte[(w / 2) * (h / 2)];
        var cr = new byte[cb.Length];
        for (int row = 0; row < h; row += 2)
        {
            int r0 = row * stride;
            int r1 = (row + 1) * stride;
            int d = (row / 2) * (w / 2);
            for (int col = 0; col < w; col += 2)
            {
                int o = col * 2;
                y[row * w + col] = src[r0 + o];
                y[row * w + col + 1] = src[r0 + o + 2];
                y[(row + 1) * w + col] = src[r1 + o];
                y[(row + 1) * w + col + 1] = src[r1 + o + 2];
                int u = src[r0 + o + 1] + src[r1 + o + 1];
                int v = src[r0 + o + 3] + src[r1 + o + 3];
                cb[d + col / 2] = (byte)(u / 2);
                cr[d + col / 2] = (byte)(v / 2);
            }
        }
        return JpegCodec.Encode420(y, cb, cr, w, h, quality);
    }

    static void Paint(byte[] y, byte[] cb, byte[] cr, int width, int x, int top, int w, int h, byte r, byte g, byte b)
    {
        Rgb(r, g, b, out var yy, out var cbb, out var crr);
        for (int row = top; row < top + h; row++)
        {
            for (int col = x; col < x + w; col++)
            {
                y[row * width + col] = yy;
                if (((row - top) & 1) == 0 && ((col - x) & 1) == 0)
                {
                    int c = (row / 2) * (width / 2) + (col / 2);
                    cb[c] = cbb;
                    cr[c] = crr;
                }
            }
        }
    }

    static void Rgb(byte r, byte g, byte b, out byte y, out byte cb, out byte cr)
    {
        y = Clamp((77 * r + 150 * g + 29 * b) >> 8);
        cb = Clamp(((-43 * r - 85 * g + 128 * b) >> 8) + 128);
        cr = Clamp(((128 * r - 107 * g - 21 * b) >> 8) + 128);
    }

    static byte Clamp(int v) => (byte)(v < 0 ? 0 : v > 255 ? 255 : v);
}
