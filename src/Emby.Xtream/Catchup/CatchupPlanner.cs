using System;
using System.Collections.Generic;
using System.Globalization;
using Emby.Xtream.Xtream;

namespace Emby.Xtream.Catchup
{
    /// <summary>Pure catch-up rules: how many days to offer and which EPG entries are playable.</summary>
    public static class CatchupPlanner
    {
        /// <summary>min(provider archive duration, configured maximum); a non-positive duration falls back to the configured maximum.</summary>
        public static int EffectiveDays(int tvArchiveDuration, int maxDays)
        {
            if (maxDays < 1) maxDays = 1;
            if (tvArchiveDuration < 1) return maxDays;
            return Math.Min(tvArchiveDuration, maxDays);
        }

        /// <summary>Server-local dates (time 00:00), today first, going back <paramref name="days"/> days.</summary>
        public static List<DateTime> DayFolders(XtreamClock clock, DateTime utcNow, int days)
        {
            var result = new List<DateTime>();
            DateTime today = clock.ToServer(utcNow).Date;
            for (int i = 0; i < days; i++) result.Add(today.AddDays(-i));
            return result;
        }

        public static string DayId(DateTime day) => day.ToString("yyyyMMdd", CultureInfo.InvariantCulture);

        public static bool TryParseDayId(string id, out DateTime day) =>
            DateTime.TryParseExact(id, "yyyyMMdd", CultureInfo.InvariantCulture, DateTimeStyles.None, out day);

        public static string DayLabel(DateTime day, DateTime serverToday)
        {
            if (day.Date == serverToday.Date) return "Today";
            if (day.Date == serverToday.Date.AddDays(-1)) return "Yesterday";
            return day.ToString("dddd d MMMM", CultureInfo.InvariantCulture);
        }

        /// <summary>
        /// Programmes of one server-local day that can be played from the archive: already finished, inside the retention window,
        /// and flagged has_archive (or, when the provider never sets the flag anywhere in the table, simply finished).
        /// </summary>
        public static List<XtreamEpgEntry> ArchivedProgrammes(IList<XtreamEpgEntry> entries, XtreamClock clock, DateTime serverDay, DateTime utcNow, int days)
        {
            var result = new List<XtreamEpgEntry>();
            if (entries == null) return result;

            bool providerFlags = false;
            foreach (var e in entries) if (e.HasArchive) { providerFlags = true; break; }

            DateTime dayStartUtc = clock.ToUtc(serverDay.Date);
            DateTime dayEndUtc = clock.ToUtc(serverDay.Date.AddDays(1));
            DateTime oldestUtc = utcNow.AddDays(-days);

            foreach (var e in entries)
            {
                if (e.StartUtc < dayStartUtc || e.StartUtc >= dayEndUtc) continue;
                if (e.StopUtc > utcNow) continue;
                if (e.StartUtc < oldestUtc) continue;
                if (providerFlags && !e.HasArchive) continue;
                result.Add(e);
            }
            result.Sort((a, b) => a.StartUtc.CompareTo(b.StartUtc));
            return result;
        }

        /// <summary>Duration in whole minutes (rounded up, minimum 1) used for the timeshift URL.</summary>
        public static int DurationMinutes(long startTs, long stopTs)
        {
            long seconds = stopTs - startTs;
            if (seconds <= 0) return 1;
            return (int)Math.Max(1, (seconds + 59) / 60);
        }
    }
}
