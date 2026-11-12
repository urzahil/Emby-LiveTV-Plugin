using System;
using System.Globalization;

namespace Emby.Xtream.Xtream
{
    /// <summary>
    /// Converts between UTC and the provider's server timezone. Xtream timeshift URLs take the start time in
    /// provider-local time. Uses server_info.timezone when resolvable, otherwise a fixed offset derived from
    /// server_info.time_now vs timestamp_now, otherwise UTC.
    /// </summary>
    public sealed class XtreamClock
    {
        private readonly TimeZoneInfo _zone;
        private readonly TimeSpan _fixedOffset;

        public static readonly XtreamClock Utc = new XtreamClock(null, TimeSpan.Zero);

        private XtreamClock(TimeZoneInfo zone, TimeSpan fixedOffset)
        {
            _zone = zone;
            _fixedOffset = fixedOffset;
        }

        public static XtreamClock FromServerInfo(XtreamServerInfo info)
        {
            if (info == null) return Utc;
            if (!string.IsNullOrWhiteSpace(info.Timezone))
            {
                try
                {
                    return new XtreamClock(TimeZoneInfo.FindSystemTimeZoneById(info.Timezone.Trim()), TimeSpan.Zero);
                }
                catch (Exception) { /* fall through to offset */ }
            }

            DateTime local;
            if (info.TimestampNow > 0 && !string.IsNullOrWhiteSpace(info.TimeNow) &&
                DateTime.TryParseExact(info.TimeNow.Trim(), "yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture, DateTimeStyles.None, out local))
            {
                DateTime utc = FromUnix(info.TimestampNow);
                double minutes = (local - utc).TotalMinutes;
                // round to the nearest 15 minutes to absorb clock skew between the two fields
                double rounded = Math.Round(minutes / 15.0) * 15.0;
                if (Math.Abs(rounded) <= 14 * 60) return new XtreamClock(null, TimeSpan.FromMinutes(rounded));
            }
            return Utc;
        }

        public DateTime ToServer(DateTime utc)
        {
            utc = DateTime.SpecifyKind(utc, DateTimeKind.Utc);
            if (_zone != null) return DateTime.SpecifyKind(TimeZoneInfo.ConvertTimeFromUtc(utc, _zone), DateTimeKind.Unspecified);
            return DateTime.SpecifyKind(utc + _fixedOffset, DateTimeKind.Unspecified);
        }

        public DateTime ToUtc(DateTime server)
        {
            server = DateTime.SpecifyKind(server, DateTimeKind.Unspecified);
            if (_zone != null)
            {
                if (_zone.IsInvalidTime(server)) server = server.AddHours(1);
                return DateTime.SpecifyKind(TimeZoneInfo.ConvertTimeToUtc(server, _zone), DateTimeKind.Utc);
            }
            return DateTime.SpecifyKind(server - _fixedOffset, DateTimeKind.Utc);
        }

        public static DateTime FromUnix(long seconds) =>
            new DateTime(1970, 1, 1, 0, 0, 0, DateTimeKind.Utc).AddSeconds(seconds);

        public static long ToUnix(DateTime utc) =>
            (long)(DateTime.SpecifyKind(utc, DateTimeKind.Utc) - new DateTime(1970, 1, 1, 0, 0, 0, DateTimeKind.Utc)).TotalSeconds;
    }
}
