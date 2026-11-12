using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Text;
using Emby.Xtream.Catchup;
using Emby.Xtream.Xtream;
using Emby.Xtream.Xtream.XmlTv;
using Xunit;

namespace Emby.Xtream.Tests
{
    public class XmlTvParserTests
    {
        private static readonly DateTime From = new DateTime(2023, 11, 14, 0, 0, 0, DateTimeKind.Utc);
        private static readonly DateTime To = new DateTime(2023, 11, 16, 0, 0, 0, DateTimeKind.Utc);

        [Fact]
        public void FiltersByChannelAndWindow()
        {
            var result = XmlTvParser.Parse(Fixture.Stream("xmltv.xml"), new HashSet<string> { "news1.example" }, From, To);
            Assert.Single(result);
            var list = result["NEWS1.example"]; // case-insensitive
            Assert.Equal(2, list.Count);          // "Too old", "Bad date" and the title-less empty element are excluded or dropped
            Assert.Equal("Evening News", list[0].Title);
        }

        [Fact]
        public void ParsesFieldsAndTimezoneOffset()
        {
            var p = XmlTvParser.Parse(Fixture.Stream("xmltv.xml"), new HashSet<string> { "news1.example" }, From, To)["news1.example"][0];
            Assert.Equal(new DateTime(2023, 11, 14, 17, 0, 0, DateTimeKind.Utc), p.StartUtc);
            Assert.Equal(new DateTime(2023, 11, 14, 18, 0, 0, DateTimeKind.Utc), p.EndUtc);
            Assert.Equal("Headlines", p.SubTitle);
            Assert.Equal("All the news & weather.", p.Description);
            Assert.Equal(new[] { "News", "Movie night" }, p.Categories.ToArray());
            Assert.Equal(2019, p.Year);
            Assert.Equal("http://img/p1.png", p.IconUrl);
            Assert.True(p.HasEpisodeNumber);
            Assert.Equal(2, p.SeasonNumber);
            Assert.Equal(5, p.EpisodeNumber);
        }

        [Fact]
        public void MissingStopUsesNextStartOrThirtyMinutes()
        {
            var list = XmlTvParser.Parse(Fixture.Stream("xmltv.xml"), new HashSet<string> { "news1.example" }, From, To)["news1.example"];
            var noStop = list[1];
            Assert.Equal("No Stop", noStop.Title);
            Assert.Equal(noStop.StartUtc.AddMinutes(30), noStop.EndUtc);
        }

        [Fact]
        public void NullFilterReturnsAllChannels()
        {
            var result = XmlTvParser.Parse(Fixture.Stream("xmltv.xml"), null, From, To);
            Assert.Equal(2, result.Count);
        }

        [Fact]
        public void ConsecutiveProgrammesAreNotSkipped()
        {
            string xml = "<tv><programme start=\"20240101100000 +0000\" stop=\"20240101110000 +0000\" channel=\"a\"><title>1</title></programme>"
                       + "<programme start=\"20240101110000 +0000\" stop=\"20240101120000 +0000\" channel=\"a\"><title>2</title></programme>"
                       + "<programme start=\"20240101120000 +0000\" stop=\"20240101130000 +0000\" channel=\"b\"><title>x</title></programme>"
                       + "<programme start=\"20240101120000 +0000\" stop=\"20240101130000 +0000\" channel=\"a\"><title>3</title></programme></tv>";
            var r = XmlTvParser.Parse(new MemoryStream(Encoding.UTF8.GetBytes(xml)), new HashSet<string> { "a" },
                new DateTime(2024, 1, 1, 0, 0, 0, DateTimeKind.Utc), new DateTime(2024, 1, 2, 0, 0, 0, DateTimeKind.Utc));
            Assert.Equal(new[] { "1", "2", "3" }, r["a"].Select(p => p.Title).ToArray());
        }

        [Fact]
        public void ReadsGzipStreams()
        {
            var raw = File.ReadAllBytes(Path.Combine(AppContext.BaseDirectory, "TestData", "xmltv.xml"));
            var ms = new MemoryStream();
            using (var gz = new GZipStream(ms, CompressionMode.Compress, true)) gz.Write(raw, 0, raw.Length);
            ms.Position = 0;
            var r = XmlTvParser.Parse(ms, new HashSet<string> { "news1.example" }, From, To);
            Assert.Equal(2, r["news1.example"].Count);
        }

        [Theory]
        [InlineData("20231114180000 +0100", 2023, 11, 14, 17, 0, 0)]
        [InlineData("20231114180000 -0530", 2023, 11, 14, 23, 30, 0)]
        [InlineData("20231114180000", 2023, 11, 14, 18, 0, 0)]
        [InlineData("20231114180000 +0000", 2023, 11, 14, 18, 0, 0)]
        [InlineData("202311141830 +0100", 2023, 11, 14, 17, 30, 0)]
        [InlineData("20231114 +0100", 2023, 11, 13, 23, 0, 0)]
        [InlineData("20231114180005 +01:00", 2023, 11, 14, 17, 0, 5)]
        [InlineData("20231114180000 Z", 2023, 11, 14, 18, 0, 0)]
        public void ParsesDates(string input, int y, int mo, int d, int h, int mi, int s)
        {
            DateTime utc;
            Assert.True(XmlTvParser.TryParseXmlTvDate(input, out utc));
            Assert.Equal(new DateTime(y, mo, d, h, mi, s, DateTimeKind.Utc), utc);
        }

        [Theory]
        [InlineData("")]
        [InlineData(null)]
        [InlineData("garbage")]
        [InlineData("2023")]
        [InlineData("20231314180000")]
        [InlineData("20231114180000 +ab")]
        public void RejectsBadDates(string input)
        {
            DateTime utc;
            Assert.False(XmlTvParser.TryParseXmlTvDate(input, out utc));
        }
    }

    public class XtreamClockTests
    {
        [Fact]
        public void UsesIanaTimezoneWithDst()
        {
            var clock = XtreamClock.FromServerInfo(new XtreamServerInfo { Timezone = "Europe/Berlin" });
            Assert.Equal(new DateTime(2023, 11, 14, 18, 0, 0), clock.ToServer(new DateTime(2023, 11, 14, 17, 0, 0, DateTimeKind.Utc)));
            Assert.Equal(new DateTime(2023, 7, 14, 19, 0, 0), clock.ToServer(new DateTime(2023, 7, 14, 17, 0, 0, DateTimeKind.Utc)));
            Assert.Equal(new DateTime(2023, 7, 14, 17, 0, 0, DateTimeKind.Utc), clock.ToUtc(new DateTime(2023, 7, 14, 19, 0, 0)));
        }

        [Fact]
        public void FallsBackToOffsetFromTimeNow()
        {
            var info = new XtreamServerInfo { Timezone = "Not/AZone", TimestampNow = 1700000000, TimeNow = "2023-11-15 00:13:21" };
            var clock = XtreamClock.FromServerInfo(info);
            // 1700000000 = 2023-11-14 22:13:20 UTC, server shows 00:13:21 => +2h
            Assert.Equal(new DateTime(2023, 11, 15, 2, 0, 0), clock.ToServer(new DateTime(2023, 11, 15, 0, 0, 0, DateTimeKind.Utc)));
        }

        [Fact]
        public void FallsBackToUtc()
        {
            var clock = XtreamClock.FromServerInfo(new XtreamServerInfo());
            var utc = new DateTime(2023, 1, 1, 10, 0, 0, DateTimeKind.Utc);
            Assert.Equal(new DateTime(2023, 1, 1, 10, 0, 0), clock.ToServer(utc));
            Assert.Equal(utc, XtreamClock.FromServerInfo(null).ToUtc(new DateTime(2023, 1, 1, 10, 0, 0)));
        }

        [Fact]
        public void UnixConversionRoundTrips()
        {
            Assert.Equal(new DateTime(2023, 11, 14, 22, 13, 20, DateTimeKind.Utc), XtreamClock.FromUnix(1700000000));
            Assert.Equal(1700000000L, XtreamClock.ToUnix(XtreamClock.FromUnix(1700000000)));
        }
    }

    public class CatchupItemIdTests
    {
        [Fact]
        public void RootIsEmptyString()
        {
            Assert.Equal(string.Empty, CatchupItemId.Root().ToString());
            Assert.Equal(CatchupNodeKind.Root, CatchupItemId.Parse(null).Kind);
            Assert.Equal(CatchupNodeKind.Root, CatchupItemId.Parse("").Kind);
        }

        [Fact]
        public void RoundTripsEveryLevel()
        {
            var ids = new[]
            {
                CatchupItemId.ForTuner("abc123"),
                CatchupItemId.ForCategory("abc123", "42"),
                CatchupItemId.ForChannel("abc123", "42", "9001"),
                CatchupItemId.ForDay("abc123", "42", "9001", "20231114"),
                CatchupItemId.ForProgramme("abc123", "42", "9001", "20231114", 1699984800, 1699988400, "News | at Six: 100% \u00e4 / ?")
            };
            foreach (var id in ids)
            {
                var parsed = CatchupItemId.Parse(id.ToString());
                Assert.NotNull(parsed);
                Assert.Equal(id.Kind, parsed.Kind);
                Assert.Equal(id.ToString(), parsed.ToString());
            }
            var p = CatchupItemId.Parse(ids[4].ToString());
            Assert.Equal("News | at Six: 100% \u00e4 / ?", p.Title);
            Assert.Equal(1699984800L, p.StartTs);
            Assert.Equal(1699988400L, p.StopTs);
            Assert.Equal("9001", p.StreamId);
            Assert.Equal("20231114", p.Day);
        }

        [Fact]
        public void IdsCarryNoCredentialsAndFollowTheDocumentedShape()
        {
            string id = CatchupItemId.ForProgramme("t1", "c1", "s1", "20240102", 10, 20, "T").ToString();
            Assert.Equal("t|t1|c|c1|s|s1|d|20240102|p|10|20|T", id);
        }

        [Theory]
        [InlineData("x")]
        [InlineData("t")]
        [InlineData("t|a|x|b")]
        [InlineData("t|a|c|b|s")]
        [InlineData("t|a|c|b|s|c|d|20240101|p|notnum|2|T")]
        [InlineData("t|a|c|b|s|c|d|20240101|p|1|2")]
        [InlineData("t|a|c|b|s|c|d|20240101|p|1|2|T|extra")]
        public void MalformedIdsReturnNull(string id)
        {
            Assert.Null(CatchupItemId.Parse(id));
        }
    }

    public class CatchupPlannerTests
    {
        private static readonly XtreamClock Berlin = XtreamClock.FromServerInfo(new XtreamServerInfo { Timezone = "Europe/Berlin" });

        private static XtreamEpgEntry E(DateTime startUtc, int minutes, bool archive, string title = "t") =>
            new XtreamEpgEntry { Title = title, StartUtc = startUtc, StopUtc = startUtc.AddMinutes(minutes), HasArchive = archive };

        [Theory]
        [InlineData(3, 7, 3)]
        [InlineData(14, 7, 7)]
        [InlineData(0, 7, 7)]
        [InlineData(-1, 7, 7)]
        [InlineData(5, 0, 1)]
        public void EffectiveDays(int provider, int max, int expected)
        {
            Assert.Equal(expected, CatchupPlanner.EffectiveDays(provider, max));
        }

        [Fact]
        public void DayFoldersAreServerLocalAndTodayFirst()
        {
            // 23:30 UTC on 14 Nov is already 15 Nov in Berlin
            var now = new DateTime(2023, 11, 14, 23, 30, 0, DateTimeKind.Utc);
            var days = CatchupPlanner.DayFolders(Berlin, now, 3);
            Assert.Equal(new[] { new DateTime(2023, 11, 15), new DateTime(2023, 11, 14), new DateTime(2023, 11, 13) }, days.ToArray());
            Assert.Equal("Today", CatchupPlanner.DayLabel(days[0], days[0]));
            Assert.Equal("Yesterday", CatchupPlanner.DayLabel(days[1], days[0]));
            Assert.Equal("Monday 13 November", CatchupPlanner.DayLabel(days[2], days[0]));
            Assert.Equal("20231115", CatchupPlanner.DayId(days[0]));
        }

        [Fact]
        public void OnlyFinishedArchivedProgrammesOfTheDay()
        {
            var now = new DateTime(2023, 11, 14, 12, 0, 0, DateTimeKind.Utc);
            var day = new DateTime(2023, 11, 14);
            var entries = new List<XtreamEpgEntry>
            {
                E(new DateTime(2023, 11, 14, 7, 0, 0, DateTimeKind.Utc), 60, true, "ok"),
                E(new DateTime(2023, 11, 14, 8, 0, 0, DateTimeKind.Utc), 60, false, "no archive flag"),
                E(new DateTime(2023, 11, 14, 11, 30, 0, DateTimeKind.Utc), 60, true, "still running"),
                E(new DateTime(2023, 11, 13, 7, 0, 0, DateTimeKind.Utc), 60, true, "other day"),
            };
            var r = CatchupPlanner.ArchivedProgrammes(entries, Berlin, day, now, 7);
            Assert.Equal(new[] { "ok" }, r.Select(x => x.Title).ToArray());
        }

        [Fact]
        public void WithoutAnyArchiveFlagsFinishedProgrammesAreOffered()
        {
            var now = new DateTime(2023, 11, 14, 12, 0, 0, DateTimeKind.Utc);
            var entries = new List<XtreamEpgEntry>
            {
                E(new DateTime(2023, 11, 14, 7, 0, 0, DateTimeKind.Utc), 60, false, "a"),
                E(new DateTime(2023, 11, 14, 8, 0, 0, DateTimeKind.Utc), 60, false, "b"),
                E(new DateTime(2023, 11, 14, 11, 30, 0, DateTimeKind.Utc), 60, false, "running")
            };
            var r = CatchupPlanner.ArchivedProgrammes(entries, Berlin, new DateTime(2023, 11, 14), now, 7);
            Assert.Equal(new[] { "a", "b" }, r.Select(x => x.Title).ToArray());
        }

        [Fact]
        public void RespectsRetentionWindow()
        {
            var now = new DateTime(2023, 11, 14, 12, 0, 0, DateTimeKind.Utc);
            var entries = new List<XtreamEpgEntry> { E(new DateTime(2023, 11, 10, 9, 0, 0, DateTimeKind.Utc), 60, true) };
            Assert.Single(CatchupPlanner.ArchivedProgrammes(entries, Berlin, new DateTime(2023, 11, 10), now, 7));
            Assert.Empty(CatchupPlanner.ArchivedProgrammes(entries, Berlin, new DateTime(2023, 11, 10), now, 3));
        }

        [Theory]
        [InlineData(0, 3600, 60)]
        [InlineData(0, 3601, 61)]
        [InlineData(0, 30, 1)]
        [InlineData(100, 50, 1)]
        public void DurationMinutes(long start, long stop, int expected)
        {
            Assert.Equal(expected, CatchupPlanner.DurationMinutes(start, stop));
        }
    }

    public class EpgTableCacheTests
    {
        [Fact]
        public async System.Threading.Tasks.Task CachesWithinTtlAndReloadsAfter()
        {
            var now = new DateTime(2024, 1, 1, 0, 0, 0, DateTimeKind.Utc);
            var cache = new EpgTableCache(2, () => now);
            int calls = 0;
            Func<System.Threading.Tasks.Task<List<XtreamEpgEntry>>> loader = () => { calls++; return System.Threading.Tasks.Task.FromResult(new List<XtreamEpgEntry>()); };

            await cache.GetAsync("k", TimeSpan.FromMinutes(30), loader, System.Threading.CancellationToken.None);
            await cache.GetAsync("k", TimeSpan.FromMinutes(30), loader, System.Threading.CancellationToken.None);
            Assert.Equal(1, calls);

            now = now.AddMinutes(31);
            await cache.GetAsync("k", TimeSpan.FromMinutes(30), loader, System.Threading.CancellationToken.None);
            Assert.Equal(2, calls);
        }

        [Fact]
        public async System.Threading.Tasks.Task ConcurrentCallersShareOneLoad()
        {
            var cache = new EpgTableCache(4);
            int calls = 0;
            Func<System.Threading.Tasks.Task<List<XtreamEpgEntry>>> loader = async () =>
            {
                System.Threading.Interlocked.Increment(ref calls);
                await System.Threading.Tasks.Task.Delay(100);
                return new List<XtreamEpgEntry>();
            };
            var tasks = Enumerable.Range(0, 8).Select(_ => cache.GetAsync("k", TimeSpan.FromMinutes(5), loader, System.Threading.CancellationToken.None)).ToArray();
            await System.Threading.Tasks.Task.WhenAll(tasks);
            Assert.Equal(1, calls);
        }
    }
}
