using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.IO.Compression;
using System.Xml;

namespace Emby.Xtream.Xtream.XmlTv
{
    public sealed class XmlTvProgram
    {
        public string ChannelId { get; set; }
        public DateTime StartUtc { get; set; }
        public DateTime EndUtc { get; set; }
        public string Title { get; set; }
        public string SubTitle { get; set; }
        public string Description { get; set; }
        public List<string> Categories { get; } = new List<string>();
        public string IconUrl { get; set; }
        public bool HasEpisodeNumber { get; set; }
        public int? SeasonNumber { get; set; }
        public int? EpisodeNumber { get; set; }
        public int? Year { get; set; }
    }

    /// <summary>Streaming XMLTV parser filtered to a set of channel ids and a time window.</summary>
    public static class XmlTvParser
    {
        /// <param name="input">XML (optionally gzip-compressed). The stream is disposed.</param>
        /// <param name="channelIds">Only these channel ids are returned (case-insensitive). Null/empty = all.</param>
        public static Dictionary<string, List<XmlTvProgram>> Parse(Stream input, ISet<string> channelIds, DateTime windowStartUtc, DateTime windowEndUtc)
        {
            var result = new Dictionary<string, List<XmlTvProgram>>(StringComparer.OrdinalIgnoreCase);
            bool filter = channelIds != null && channelIds.Count > 0;

            using (var stream = MaybeGunzip(input))
            {
                var settings = new XmlReaderSettings
                {
                    DtdProcessing = DtdProcessing.Ignore,
                    XmlResolver = null,
                    CheckCharacters = false,
                    IgnoreComments = true,
                    IgnoreWhitespace = true
                };
                using (var reader = XmlReader.Create(stream, settings))
                {
                    reader.MoveToContent();
                    while (!reader.EOF)
                    {
                        if (reader.NodeType != XmlNodeType.Element || reader.Name != "programme")
                        {
                            reader.Read();
                            continue;
                        }

                        string channel = reader.GetAttribute("channel");
                        string startAttr = reader.GetAttribute("start");
                        string stopAttr = reader.GetAttribute("stop");

                        bool wanted = !string.IsNullOrEmpty(channel) && (!filter || channelIds.Contains(channel));
                        DateTime start, stop = default(DateTime);
                        if (!wanted || !TryParseXmlTvDate(startAttr, out start))
                        {
                            reader.Skip();   // positions on the next sibling; do not Read() again
                            continue;
                        }
                        bool hasStop = TryParseXmlTvDate(stopAttr, out stop);

                        var prog = new XmlTvProgram { ChannelId = channel, StartUtc = start, EndUtc = hasStop ? stop : default(DateTime) };
                        ReadProgrammeBody(reader, prog);

                        List<XmlTvProgram> list;
                        if (!result.TryGetValue(channel, out list)) { list = new List<XmlTvProgram>(); result[channel] = list; }
                        list.Add(prog);
                    }
                }
            }

            // sort, fill missing end times from the next start, apply the window
            var keys = new List<string>(result.Keys);
            foreach (var key in keys)
            {
                var list = result[key];
                list.RemoveAll(p => string.IsNullOrWhiteSpace(p.Title));
                list.Sort((a, b) => a.StartUtc.CompareTo(b.StartUtc));
                for (int i = 0; i < list.Count; i++)
                {
                    if (list[i].EndUtc <= list[i].StartUtc)
                        list[i].EndUtc = i + 1 < list.Count ? list[i + 1].StartUtc : list[i].StartUtc.AddMinutes(30);
                }
                list.RemoveAll(p => p.EndUtc <= p.StartUtc || p.EndUtc <= windowStartUtc || p.StartUtc >= windowEndUtc);
                if (list.Count == 0) result.Remove(key);
            }
            return result;
        }

        /// <summary>Consumes the whole programme element; leaves the reader on the node after it.</summary>
        private static void ReadProgrammeBody(XmlReader reader, XmlTvProgram prog)
        {
            if (reader.IsEmptyElement) { reader.Read(); return; }
            int depth = reader.Depth;
            reader.Read(); // into content
            while (!reader.EOF && !(reader.NodeType == XmlNodeType.EndElement && reader.Depth == depth))
            {
                if (reader.NodeType != XmlNodeType.Element) { reader.Read(); continue; }

                // every branch below consumes the element completely
                switch (reader.Name)
                {
                    case "title":
                        { string v = SafeReadString(reader); if (prog.Title == null && !string.IsNullOrWhiteSpace(v)) prog.Title = v.Trim(); }
                        break;
                    case "sub-title":
                        { string v = SafeReadString(reader); if (prog.SubTitle == null && !string.IsNullOrWhiteSpace(v)) prog.SubTitle = v.Trim(); }
                        break;
                    case "desc":
                        { string v = SafeReadString(reader); if (prog.Description == null && !string.IsNullOrWhiteSpace(v)) prog.Description = v.Trim(); }
                        break;
                    case "category":
                        { string v = SafeReadString(reader); if (!string.IsNullOrWhiteSpace(v)) prog.Categories.Add(v.Trim()); }
                        break;
                    case "date":
                        {
                            string v = SafeReadString(reader);
                            int year;
                            if (v != null && v.Trim().Length >= 4 && IsFourDigits(v.Trim().Substring(0, 4)) &&
                                int.TryParse(v.Trim().Substring(0, 4), NumberStyles.None, CultureInfo.InvariantCulture, out year) && year >= 1800 && year < 2200)
                                prog.Year = year;
                        }
                        break;
                    case "icon":
                        {
                            string src = reader.GetAttribute("src");
                            if (prog.IconUrl == null && !string.IsNullOrWhiteSpace(src)) prog.IconUrl = src.Trim();
                            reader.Skip();
                        }
                        break;
                    case "episode-num":
                        {
                            string system = reader.GetAttribute("system");
                            string v = SafeReadString(reader);
                            if (!string.IsNullOrWhiteSpace(v))
                            {
                                prog.HasEpisodeNumber = true;
                                if (string.Equals(system, "xmltv_ns", StringComparison.OrdinalIgnoreCase)) ParseXmltvNs(v, prog);
                            }
                        }
                        break;
                    default:
                        reader.Skip();
                        break;
                }
            }
            // reader is on the programme end tag: step past it
            if (!reader.EOF) reader.Read();
        }

        private static bool IsFourDigits(string s)
        {
            if (s.Length != 4) return false;
            foreach (char c in s) if (c < '0' || c > '9') return false;
            return true;
        }

        /// <summary>Reads element text and leaves the reader positioned after the element.</summary>
        private static string SafeReadString(XmlReader reader)
        {
            try { return reader.ReadElementContentAsString(); }
            catch (XmlException) { reader.Skip(); return null; }
        }

        // "S.E.P/total" zero-based, e.g. "1.4." -> season 2 episode 5
        private static void ParseXmltvNs(string value, XmlTvProgram prog)
        {
            var parts = value.Split('.');
            if (parts.Length >= 1) prog.SeasonNumber = ParseNsPart(parts[0]);
            if (parts.Length >= 2) prog.EpisodeNumber = ParseNsPart(parts[1]);
        }

        private static int? ParseNsPart(string p)
        {
            int slash = p.IndexOf('/');
            if (slash >= 0) p = p.Substring(0, slash);
            int n;
            if (int.TryParse(p.Trim(), NumberStyles.None, CultureInfo.InvariantCulture, out n)) return n + 1;
            return null;
        }

        private static Stream MaybeGunzip(Stream input) => new PeekGzipStream(input);

        // Wraps a non-seekable stream, sniffing for the gzip magic number.
        private sealed class PeekGzipStream : Stream
        {
            private readonly Stream _inner;

            public PeekGzipStream(Stream source)
            {
                var head = new byte[2];
                int n = 0;
                while (n < 2)
                {
                    int r = source.Read(head, n, 2 - n);
                    if (r <= 0) break;
                    n += r;
                }
                var prefixed = new PrefixedStream(head, n, source);
                _inner = (n == 2 && head[0] == 0x1f && head[1] == 0x8b)
                    ? (Stream)new GZipStream(prefixed, CompressionMode.Decompress)
                    : prefixed;
            }

            public override bool CanRead => true;
            public override bool CanSeek => false;
            public override bool CanWrite => false;
            public override long Length => throw new NotSupportedException();
            public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
            public override void Flush() { }
            public override int Read(byte[] buffer, int offset, int count) => _inner.Read(buffer, offset, count);
            public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
            public override void SetLength(long value) => throw new NotSupportedException();
            public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
            protected override void Dispose(bool disposing) { if (disposing) _inner.Dispose(); base.Dispose(disposing); }
        }

        private sealed class PrefixedStream : Stream
        {
            private readonly byte[] _prefix;
            private readonly int _prefixLen;
            private int _prefixPos;
            private readonly Stream _rest;

            public PrefixedStream(byte[] prefix, int prefixLen, Stream rest) { _prefix = prefix; _prefixLen = prefixLen; _rest = rest; }

            public override bool CanRead => true;
            public override bool CanSeek => false;
            public override bool CanWrite => false;
            public override long Length => throw new NotSupportedException();
            public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
            public override void Flush() { }
            public override int Read(byte[] buffer, int offset, int count)
            {
                if (_prefixPos < _prefixLen)
                {
                    int n = Math.Min(count, _prefixLen - _prefixPos);
                    Buffer.BlockCopy(_prefix, _prefixPos, buffer, offset, n);
                    _prefixPos += n;
                    return n;
                }
                return _rest.Read(buffer, offset, count);
            }
            public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
            public override void SetLength(long value) => throw new NotSupportedException();
            public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
            protected override void Dispose(bool disposing) { if (disposing) _rest.Dispose(); base.Dispose(disposing); }
        }

        // ---- date parsing --------------------------------------------------------

        /// <summary>
        /// XMLTV time: "YYYYMMDDhhmmss[ +hhmm]" (seconds, minutes and offset optional). No offset means UTC.
        /// </summary>
        public static bool TryParseXmlTvDate(string value, out DateTime utc)
        {
            utc = default(DateTime);
            if (string.IsNullOrWhiteSpace(value)) return false;
            value = value.Trim();

            int i = 0;
            while (i < value.Length && value[i] >= '0' && value[i] <= '9') i++;
            if (i < 8) return false;
            string digits = value.Substring(0, i);
            string rest = value.Substring(i).Trim();

            int year, month, day, hour = 0, minute = 0, second = 0;
            if (!int.TryParse(digits.Substring(0, 4), NumberStyles.None, CultureInfo.InvariantCulture, out year)) return false;
            if (!int.TryParse(digits.Substring(4, 2), NumberStyles.None, CultureInfo.InvariantCulture, out month)) return false;
            if (!int.TryParse(digits.Substring(6, 2), NumberStyles.None, CultureInfo.InvariantCulture, out day)) return false;
            if (digits.Length >= 10 && !int.TryParse(digits.Substring(8, 2), NumberStyles.None, CultureInfo.InvariantCulture, out hour)) return false;
            if (digits.Length >= 12 && !int.TryParse(digits.Substring(10, 2), NumberStyles.None, CultureInfo.InvariantCulture, out minute)) return false;
            if (digits.Length >= 14 && !int.TryParse(digits.Substring(12, 2), NumberStyles.None, CultureInfo.InvariantCulture, out second)) return false;

            TimeSpan offset = TimeSpan.Zero;
            if (rest.Length > 0)
            {
                if (rest.Equals("Z", StringComparison.OrdinalIgnoreCase) || rest.Equals("UTC", StringComparison.OrdinalIgnoreCase) || rest.Equals("GMT", StringComparison.OrdinalIgnoreCase))
                {
                    offset = TimeSpan.Zero;
                }
                else if (rest[0] == '+' || rest[0] == '-')
                {
                    string o = rest.Substring(1).Replace(":", string.Empty);
                    int oh, om = 0;
                    if (o.Length < 2 || !int.TryParse(o.Substring(0, 2), NumberStyles.None, CultureInfo.InvariantCulture, out oh)) return false;
                    if (o.Length >= 4 && !int.TryParse(o.Substring(2, 2), NumberStyles.None, CultureInfo.InvariantCulture, out om)) return false;
                    offset = new TimeSpan(oh, om, 0);
                    if (rest[0] == '-') offset = -offset;
                }
                else return false;
            }

            try
            {
                var local = new DateTime(year, month, day, hour, minute, second, DateTimeKind.Unspecified);
                utc = DateTime.SpecifyKind(local - offset, DateTimeKind.Utc);
                return true;
            }
            catch (ArgumentOutOfRangeException) { return false; }
        }
    }
}
