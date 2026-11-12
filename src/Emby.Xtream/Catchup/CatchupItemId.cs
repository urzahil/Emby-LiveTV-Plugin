using System;
using System.Globalization;
using System.Text;

namespace Emby.Xtream.Catchup
{
    public enum CatchupNodeKind
    {
        Root,
        Tuner,
        Category,
        Channel,
        Day,
        Programme
    }

    /// <summary>
    /// Encodes/decodes folder and item ids of the catch-up channel tree. Ids never contain credentials.
    /// Layout: t|{tuner}|c|{cat}|s|{stream}|d|{yyyyMMdd}|p|{startTs}|{stopTs}|{title}  (each value URL-escaped).
    /// A node id is a prefix of that sequence; the root is the empty folder id.
    /// </summary>
    public sealed class CatchupItemId
    {
        public CatchupNodeKind Kind { get; private set; }
        public string TunerId { get; private set; }
        public string CategoryId { get; private set; }
        public string StreamId { get; private set; }
        public string Day { get; private set; }
        public long StartTs { get; private set; }
        public long StopTs { get; private set; }
        public string Title { get; private set; }

        public static CatchupItemId Root() => new CatchupItemId { Kind = CatchupNodeKind.Root };

        public static CatchupItemId ForTuner(string tunerId) =>
            new CatchupItemId { Kind = CatchupNodeKind.Tuner, TunerId = tunerId };

        public static CatchupItemId ForCategory(string tunerId, string categoryId) =>
            new CatchupItemId { Kind = CatchupNodeKind.Category, TunerId = tunerId, CategoryId = categoryId };

        public static CatchupItemId ForChannel(string tunerId, string categoryId, string streamId) =>
            new CatchupItemId { Kind = CatchupNodeKind.Channel, TunerId = tunerId, CategoryId = categoryId, StreamId = streamId };

        public static CatchupItemId ForDay(string tunerId, string categoryId, string streamId, string yyyymmdd) =>
            new CatchupItemId { Kind = CatchupNodeKind.Day, TunerId = tunerId, CategoryId = categoryId, StreamId = streamId, Day = yyyymmdd };

        public static CatchupItemId ForProgramme(string tunerId, string categoryId, string streamId, string yyyymmdd, long startTs, long stopTs, string title) =>
            new CatchupItemId
            {
                Kind = CatchupNodeKind.Programme, TunerId = tunerId, CategoryId = categoryId, StreamId = streamId,
                Day = yyyymmdd, StartTs = startTs, StopTs = stopTs, Title = title ?? string.Empty
            };

        public override string ToString()
        {
            if (Kind == CatchupNodeKind.Root) return string.Empty;
            var sb = new StringBuilder();
            sb.Append("t|").Append(E(TunerId));
            if (Kind == CatchupNodeKind.Tuner) return sb.ToString();
            sb.Append("|c|").Append(E(CategoryId));
            if (Kind == CatchupNodeKind.Category) return sb.ToString();
            sb.Append("|s|").Append(E(StreamId));
            if (Kind == CatchupNodeKind.Channel) return sb.ToString();
            sb.Append("|d|").Append(E(Day));
            if (Kind == CatchupNodeKind.Day) return sb.ToString();
            sb.Append("|p|").Append(StartTs.ToString(CultureInfo.InvariantCulture))
              .Append('|').Append(StopTs.ToString(CultureInfo.InvariantCulture))
              .Append('|').Append(E(Title));
            return sb.ToString();
        }

        /// <summary>Parses an id; null/empty = root; returns null when malformed.</summary>
        public static CatchupItemId Parse(string id)
        {
            if (string.IsNullOrEmpty(id)) return Root();
            string[] p = id.Split('|');
            try
            {
                if (p.Length < 2 || p[0] != "t") return null;
                string tuner = U(p[1]);
                if (p.Length == 2) return ForTuner(tuner);
                if (p.Length < 4 || p[2] != "c") return null;
                string cat = U(p[3]);
                if (p.Length == 4) return ForCategory(tuner, cat);
                if (p.Length < 6 || p[4] != "s") return null;
                string stream = U(p[5]);
                if (p.Length == 6) return ForChannel(tuner, cat, stream);
                if (p.Length < 8 || p[6] != "d") return null;
                string day = U(p[7]);
                if (p.Length == 8) return ForDay(tuner, cat, stream, day);
                if (p.Length != 12 || p[8] != "p") return null;
                long start, stop;
                if (!long.TryParse(p[9], NumberStyles.Integer, CultureInfo.InvariantCulture, out start)) return null;
                if (!long.TryParse(p[10], NumberStyles.Integer, CultureInfo.InvariantCulture, out stop)) return null;
                return ForProgramme(tuner, cat, stream, day, start, stop, U(p[11]));
            }
            catch (UriFormatException) { return null; }
        }

        private static string E(string s) => Uri.EscapeDataString(s ?? string.Empty);
        private static string U(string s) => Uri.UnescapeDataString(s);
    }
}
