using System;
using System.IO;
using System.IO.Compression;

namespace Emby.Xtream.Catchup
{
    /// <summary>
    /// Keeps channel logos used as catch-up pictures within a safe shape. The Emby Android TV app blurs an item's picture
    /// with RenderScript for the detail-screen background, and on some devices that crashes the app natively
    /// (rsdAllocationGenerateMipmaps) for very wide images such as a 512×77 banner logo. Padding the logo with transparent
    /// pixels to at most 16:9 (or at least 1:2) avoids it. Only PNG is re-encoded; other formats are measured only.
    /// </summary>
    public static class LogoPadder
    {
        public const double MaxAspect = 16.0 / 9.0;
        public const double MinAspect = 1.0 / 2.0;
        private const int MaxPixels = 4096 * 4096;
        private static readonly byte[] PngSignature = { 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A };

        public enum Verdict
        {
            /// <summary>Shape is fine, or the format is unknown: use the logo as it is.</summary>
            KeepOriginal,
            /// <summary>Use <c>padded</c> instead.</summary>
            UsePadded,
            /// <summary>Too wide or tall and cannot be padded: give the item no picture.</summary>
            Drop
        }

        /// <summary>Decides what to do with a downloaded logo; <paramref name="padded"/> is set for <see cref="Verdict.UsePadded"/>.</summary>
        public static Verdict Process(byte[] data, out byte[] padded)
        {
            padded = null;
            int width, height;
            bool isPng = IsPng(data);
            if (isPng) { if (!TryPngSize(data, out width, out height)) return Verdict.KeepOriginal; }
            else if (!TryJpegSize(data, out width, out height)) return Verdict.KeepOriginal;

            int targetWidth, targetHeight;
            if (!NeedsPadding(width, height, out targetWidth, out targetHeight)) return Verdict.KeepOriginal;
            if (!isPng) return Verdict.Drop;

            try
            {
                padded = PadPng(data, targetWidth, targetHeight);
                return padded == null ? Verdict.Drop : Verdict.UsePadded;
            }
            catch (Exception)
            {
                return Verdict.Drop; // corrupt or unsupported PNG
            }
        }

        public static bool NeedsPadding(int width, int height, out int targetWidth, out int targetHeight)
        {
            targetWidth = width;
            targetHeight = height;
            if (width <= 0 || height <= 0) return false;
            double aspect = (double)width / height;
            if (aspect > MaxAspect) targetHeight = (int)Math.Ceiling(width / MaxAspect);
            else if (aspect < MinAspect) targetWidth = (int)Math.Ceiling(height * MinAspect);
            else return false;
            return true;
        }

        public static bool IsPng(byte[] d)
        {
            if (d == null || d.Length < PngSignature.Length) return false;
            for (int i = 0; i < PngSignature.Length; i++) if (d[i] != PngSignature[i]) return false;
            return true;
        }

        public static bool TryPngSize(byte[] d, out int width, out int height)
        {
            width = height = 0;
            if (!IsPng(d) || d.Length < 24 || ReadAscii(d, 12, 4) != "IHDR") return false;
            width = ReadInt(d, 16);
            height = ReadInt(d, 20);
            return width > 0 && height > 0;
        }

        /// <summary>Reads the frame size from a JPEG's SOF marker.</summary>
        public static bool TryJpegSize(byte[] d, out int width, out int height)
        {
            width = height = 0;
            if (d == null || d.Length < 4 || d[0] != 0xFF || d[1] != 0xD8) return false;
            int i = 2;
            while (i + 9 < d.Length)
            {
                if (d[i] != 0xFF) return false;
                byte marker = d[i + 1];
                if (marker == 0xFF) { i++; continue; }
                if (marker == 0xD8 || marker == 0x01 || (marker >= 0xD0 && marker <= 0xD7)) { i += 2; continue; }
                int length = (d[i + 2] << 8) | d[i + 3];
                bool sof = marker >= 0xC0 && marker <= 0xCF && marker != 0xC4 && marker != 0xC8 && marker != 0xCC;
                if (sof)
                {
                    height = (d[i + 5] << 8) | d[i + 6];
                    width = (d[i + 7] << 8) | d[i + 8];
                    return width > 0 && height > 0;
                }
                if (length < 2) return false;
                i += 2 + length;
            }
            return false;
        }

        // ---- PNG decode -> pad -> encode ------------------------------------------------

        /// <summary>Returns the PNG centred on a transparent canvas of the target size, or null when the PNG kind is unsupported.</summary>
        public static byte[] PadPng(byte[] png, int targetWidth, int targetHeight)
        {
            int width, height;
            byte[] rgba = DecodeToRgba(png, out width, out height);
            if (rgba == null) return null;
            if ((long)targetWidth * targetHeight > MaxPixels) return null;

            var canvas = new byte[targetWidth * targetHeight * 4]; // zeroed = fully transparent
            int left = (targetWidth - width) / 2, top = (targetHeight - height) / 2;
            for (int y = 0; y < height; y++)
                Buffer.BlockCopy(rgba, y * width * 4, canvas, ((top + y) * targetWidth + left) * 4, width * 4);
            return EncodeRgba(canvas, targetWidth, targetHeight);
        }

        /// <summary>Decodes a non-interlaced PNG of any colour type to 8-bit RGBA; null when unsupported (interlaced, too big).</summary>
        public static byte[] DecodeToRgba(byte[] png, out int width, out int height)
        {
            width = height = 0;
            if (!IsPng(png)) return null;

            int bitDepth = 0, colorType = 0, interlace = 0;
            byte[] palette = null, trns = null;
            var idat = new MemoryStream();
            int pos = 8;
            while (pos + 12 <= png.Length)
            {
                int length = ReadInt(png, pos);
                string type = ReadAscii(png, pos + 4, 4);
                int data = pos + 8;
                if (length < 0 || data + length > png.Length) return null;
                switch (type)
                {
                    case "IHDR":
                        width = ReadInt(png, data);
                        height = ReadInt(png, data + 4);
                        bitDepth = png[data + 8];
                        colorType = png[data + 9];
                        interlace = png[data + 12];
                        break;
                    case "PLTE":
                        palette = new byte[length];
                        Buffer.BlockCopy(png, data, palette, 0, length);
                        break;
                    case "tRNS":
                        trns = new byte[length];
                        Buffer.BlockCopy(png, data, trns, 0, length);
                        break;
                    case "IDAT":
                        idat.Write(png, data, length);
                        break;
                }
                if (type == "IEND") break;
                pos = data + length + 4; // skip CRC
            }

            if (width <= 0 || height <= 0 || (long)width * height > MaxPixels || interlace != 0) return null;
            int channels;
            switch (colorType)
            {
                case 0: channels = 1; break;
                case 2: channels = 3; break;
                case 3: channels = 1; if (palette == null) return null; break;
                case 4: channels = 2; break;
                case 6: channels = 4; break;
                default: return null;
            }
            if (bitDepth != 1 && bitDepth != 2 && bitDepth != 4 && bitDepth != 8 && bitDepth != 16) return null;
            if (bitDepth < 8 && colorType != 0 && colorType != 3) return null;
            if (bitDepth == 16 && colorType == 3) return null;

            int bitsPerPixel = channels * bitDepth;
            int stride = (width * bitsPerPixel + 7) / 8;
            int bpp = Math.Max(1, bitsPerPixel / 8);
            byte[] raw = Inflate(idat.ToArray(), (stride + 1) * height);
            if (raw == null) return null;

            var rgba = new byte[width * height * 4];
            var prev = new byte[stride];
            var line = new byte[stride];
            for (int y = 0; y < height; y++)
            {
                int rowStart = y * (stride + 1);
                byte filter = raw[rowStart];
                Buffer.BlockCopy(raw, rowStart + 1, line, 0, stride);
                Unfilter(filter, line, prev, bpp);
                for (int x = 0; x < width; x++) WritePixel(line, x, bitDepth, colorType, palette, trns, rgba, (y * width + x) * 4);
                var t = prev; prev = line; line = t;
            }
            return rgba;
        }

        private static void Unfilter(byte filter, byte[] line, byte[] prev, int bpp)
        {
            int n = line.Length;
            switch (filter)
            {
                case 0: return;
                case 1: for (int i = bpp; i < n; i++) line[i] += line[i - bpp]; return;
                case 2: for (int i = 0; i < n; i++) line[i] += prev[i]; return;
                case 3:
                    for (int i = 0; i < n; i++) line[i] += (byte)(((i >= bpp ? line[i - bpp] : 0) + prev[i]) / 2);
                    return;
                case 4:
                    for (int i = 0; i < n; i++)
                    {
                        int a = i >= bpp ? line[i - bpp] : 0, b = prev[i], c = i >= bpp ? prev[i - bpp] : 0;
                        int p = a + b - c, pa = Math.Abs(p - a), pb = Math.Abs(p - b), pc = Math.Abs(p - c);
                        line[i] += (byte)(pa <= pb && pa <= pc ? a : pb <= pc ? b : c);
                    }
                    return;
                default: throw new InvalidDataException("Unknown PNG filter " + filter);
            }
        }

        private static void WritePixel(byte[] line, int x, int bitDepth, int colorType, byte[] palette, byte[] trns, byte[] rgba, int o)
        {
            if (colorType == 3 || (colorType == 0 && bitDepth < 8))
            {
                int perByte = 8 / bitDepth;
                int value = bitDepth == 8 ? line[x] : (line[x / perByte] >> ((perByte - 1 - x % perByte) * bitDepth)) & ((1 << bitDepth) - 1);
                if (colorType == 3)
                {
                    int p = value * 3;
                    if (p + 2 < palette.Length) { rgba[o] = palette[p]; rgba[o + 1] = palette[p + 1]; rgba[o + 2] = palette[p + 2]; }
                    rgba[o + 3] = trns != null && value < trns.Length ? trns[value] : (byte)255;
                }
                else
                {
                    byte g = (byte)(value * 255 / ((1 << bitDepth) - 1));
                    rgba[o] = rgba[o + 1] = rgba[o + 2] = g;
                    rgba[o + 3] = trns != null && trns.Length >= 2 && ((trns[0] << 8) | trns[1]) == value ? (byte)0 : (byte)255;
                }
                return;
            }

            int step = bitDepth / 8; // 1 or 2 bytes per sample; for 16-bit the high byte is used
            int s;
            switch (colorType)
            {
                case 0:
                    s = x * step;
                    rgba[o] = rgba[o + 1] = rgba[o + 2] = line[s];
                    rgba[o + 3] = trns != null && trns.Length >= 2 && Sample(line, s, step) == ((trns[0] << 8) | trns[1]) ? (byte)0 : (byte)255;
                    return;
                case 2:
                    s = x * 3 * step;
                    rgba[o] = line[s]; rgba[o + 1] = line[s + step]; rgba[o + 2] = line[s + 2 * step];
                    rgba[o + 3] = trns != null && trns.Length >= 6
                        && Sample(line, s, step) == ((trns[0] << 8) | trns[1])
                        && Sample(line, s + step, step) == ((trns[2] << 8) | trns[3])
                        && Sample(line, s + 2 * step, step) == ((trns[4] << 8) | trns[5]) ? (byte)0 : (byte)255;
                    return;
                case 4:
                    s = x * 2 * step;
                    rgba[o] = rgba[o + 1] = rgba[o + 2] = line[s];
                    rgba[o + 3] = line[s + step];
                    return;
                default: // 6
                    s = x * 4 * step;
                    rgba[o] = line[s]; rgba[o + 1] = line[s + step]; rgba[o + 2] = line[s + 2 * step]; rgba[o + 3] = line[s + 3 * step];
                    return;
            }
        }

        private static int Sample(byte[] line, int s, int step) => step == 2 ? (line[s] << 8) | line[s + 1] : line[s];

        private static byte[] Inflate(byte[] zlib, int expected)
        {
            if (zlib.Length < 2) return null;
            using (var input = new MemoryStream(zlib, 2, zlib.Length - 2)) // skip the zlib header; DeflateStream wants raw deflate
            using (var inflater = new DeflateStream(input, CompressionMode.Decompress))
            {
                var output = new byte[expected];
                int total = 0;
                while (total < expected)
                {
                    int n = inflater.Read(output, total, expected - total);
                    if (n == 0) break;
                    total += n;
                }
                return total == expected ? output : null;
            }
        }

        /// <summary>Encodes 8-bit RGBA as a PNG (filter 0 on every row, one IDAT).</summary>
        public static byte[] EncodeRgba(byte[] rgba, int width, int height)
        {
            byte[] deflated;
            using (var raw = new MemoryStream())
            {
                using (var deflater = new DeflateStream(raw, CompressionLevel.Optimal, leaveOpen: true))
                {
                    for (int y = 0; y < height; y++)
                    {
                        deflater.WriteByte(0);
                        deflater.Write(rgba, y * width * 4, width * 4);
                    }
                }
                deflated = raw.ToArray();
            }

            uint adler = Adler32(rgba, width, height);
            using (var png = new MemoryStream())
            {
                png.Write(PngSignature, 0, PngSignature.Length);
                var ihdr = new byte[13];
                WriteInt(ihdr, 0, width);
                WriteInt(ihdr, 4, height);
                ihdr[8] = 8;  // bit depth
                ihdr[9] = 6;  // RGBA
                WriteChunk(png, "IHDR", ihdr);

                var idat = new byte[2 + deflated.Length + 4];
                idat[0] = 0x78; idat[1] = 0x9C; // zlib header, default compression
                Buffer.BlockCopy(deflated, 0, idat, 2, deflated.Length);
                WriteInt(idat, idat.Length - 4, (int)adler);
                WriteChunk(png, "IDAT", idat);
                WriteChunk(png, "IEND", new byte[0]);
                return png.ToArray();
            }
        }

        private static uint Adler32(byte[] rgba, int width, int height)
        {
            uint a = 1, b = 0;
            for (int y = 0; y < height; y++)
            {
                a = a % 65521; b = (b + a) % 65521; // the filter byte 0
                int start = y * width * 4, end = start + width * 4;
                for (int i = start; i < end; i++)
                {
                    a = (a + rgba[i]) % 65521;
                    b = (b + a) % 65521;
                }
            }
            return (b << 16) | a;
        }

        private static void WriteChunk(Stream s, string type, byte[] data)
        {
            var header = new byte[8];
            WriteInt(header, 0, data.Length);
            for (int i = 0; i < 4; i++) header[4 + i] = (byte)type[i];
            s.Write(header, 0, 8);
            s.Write(data, 0, data.Length);
            uint crc = Crc32(header, 4, 4, 0xFFFFFFFFu);
            crc = Crc32(data, 0, data.Length, crc) ^ 0xFFFFFFFFu;
            var c = new byte[4];
            WriteInt(c, 0, (int)crc);
            s.Write(c, 0, 4);
        }

        private static readonly uint[] CrcTable = BuildCrcTable();

        private static uint[] BuildCrcTable()
        {
            var table = new uint[256];
            for (uint n = 0; n < 256; n++)
            {
                uint c = n;
                for (int k = 0; k < 8; k++) c = (c & 1) != 0 ? 0xEDB88320u ^ (c >> 1) : c >> 1;
                table[n] = c;
            }
            return table;
        }

        private static uint Crc32(byte[] d, int offset, int count, uint crc)
        {
            for (int i = offset; i < offset + count; i++) crc = CrcTable[(crc ^ d[i]) & 0xFF] ^ (crc >> 8);
            return crc;
        }

        private static int ReadInt(byte[] d, int o) => (d[o] << 24) | (d[o + 1] << 16) | (d[o + 2] << 8) | d[o + 3];

        private static void WriteInt(byte[] d, int o, int v)
        {
            d[o] = (byte)(v >> 24); d[o + 1] = (byte)(v >> 16); d[o + 2] = (byte)(v >> 8); d[o + 3] = (byte)v;
        }

        private static string ReadAscii(byte[] d, int o, int n)
        {
            if (o + n > d.Length) return string.Empty;
            var c = new char[n];
            for (int i = 0; i < n; i++) c[i] = (char)d[o + i];
            return new string(c);
        }
    }
}
