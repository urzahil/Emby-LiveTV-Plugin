using System;
using System.Globalization;

namespace Emby.Xtream.Catchup
{
    /// <summary>A single HTTP byte range ("bytes=from-" or "bytes=from-to").</summary>
    public sealed class ByteRange
    {
        public long From { get; private set; }
        public long? To { get; private set; }

        public ByteRange(long from, long? to)
        {
            From = from;
            To = to;
        }

        /// <summary>Parses a Range header; null for none, multiple ranges, suffix ranges or anything malformed.</summary>
        public static ByteRange Parse(string header)
        {
            if (string.IsNullOrWhiteSpace(header)) return null;
            header = header.Trim();
            if (!header.StartsWith("bytes=", StringComparison.OrdinalIgnoreCase)) return null;
            string spec = header.Substring(6).Trim();
            if (spec.IndexOf(',') >= 0) return null;
            int dash = spec.IndexOf('-');
            if (dash <= 0) return null; // "-500" (suffix) is not supported
            long from;
            if (!long.TryParse(spec.Substring(0, dash).Trim(), NumberStyles.None, CultureInfo.InvariantCulture, out from)) return null;
            string toText = spec.Substring(dash + 1).Trim();
            if (toText.Length == 0) return new ByteRange(from, null);
            long to;
            if (!long.TryParse(toText, NumberStyles.None, CultureInfo.InvariantCulture, out to) || to < from) return null;
            return new ByteRange(from, to);
        }

        /// <summary>Last byte served for a resource of <paramref name="length"/> bytes.</summary>
        public long LastByte(long length) => To.HasValue ? Math.Min(To.Value, length - 1) : length - 1;

        /// <summary>"bytes=a-b" for the same range shifted by <paramref name="shift"/> bytes.</summary>
        public string ToHeader(long shift) =>
            "bytes=" + (From + shift).ToString(CultureInfo.InvariantCulture) + "-" +
            (To.HasValue ? (To.Value + shift).ToString(CultureInfo.InvariantCulture) : string.Empty);
    }
}
