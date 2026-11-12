using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Emby.Xtream.Xtream;
using Emby.Xtream.Xtream.Json;
using Xunit;

namespace Emby.Xtream.Tests
{
    public class XtreamClientTests
    {
        private static XtreamClient Client(FakeHttp http) => new XtreamClient(http, new XtreamCredentials("http://h:8080/", "u", "p w"));

        [Fact]
        public async Task ParsesAccount()
        {
            var http = new FakeHttp().Route("player_api.php?username=u&password=p%20w", Fixture.Text("player_api.json"));
            var acc = await Client(http).GetAccountAsync(CancellationToken.None);
            Assert.True(acc.UserInfo.Auth);
            Assert.Equal("Active", acc.UserInfo.Status);
            Assert.Equal(1893456000L, acc.UserInfo.ExpDate);
            Assert.Equal(2, acc.UserInfo.MaxConnections);
            Assert.Equal(new[] { "m3u8", "ts" }, acc.UserInfo.AllowedOutputFormats.ToArray());
            Assert.Equal("Europe/Berlin", acc.ServerInfo.Timezone);
            Assert.Equal(1700000000L, acc.ServerInfo.TimestampNow);
            Assert.Null(XtreamClient.GetAccountProblem(acc, new DateTime(2024, 1, 1, 0, 0, 0, DateTimeKind.Utc)));
        }

        [Theory]
        [InlineData("{\"user_info\":{\"auth\":0}}", "Authentication failed")]
        [InlineData("{\"user_info\":{\"auth\":1,\"status\":\"Expired\"}}", "Expired")]
        [InlineData("{\"user_info\":{\"auth\":1,\"status\":\"Active\",\"exp_date\":\"1000000000\"}}", "expired on")]
        [InlineData("{}", "Authentication failed")]
        [InlineData("[]", "Authentication failed")]
        public void AccountProblems(string json, string expectedFragment)
        {
            var acc = XtreamClient.ParseAccount(JsonLite.Parse(json));
            var problem = XtreamClient.GetAccountProblem(acc, DateTime.UtcNow);
            Assert.NotNull(problem);
            Assert.Contains(expectedFragment, problem);
        }

        [Fact]
        public void UnlimitedExpiryIsNull()
        {
            var acc = XtreamClient.ParseAccount(JsonLite.Parse("{\"user_info\":{\"auth\":1,\"status\":\"Active\",\"exp_date\":null}}"));
            Assert.Null(acc.UserInfo.ExpDate);
            Assert.Null(XtreamClient.GetAccountProblem(acc, DateTime.UtcNow));
        }

        [Fact]
        public async Task ParsesCategoriesWithMixedTypes()
        {
            var http = new FakeHttp().Route("get_live_categories", Fixture.Text("live_categories.json"));
            var cats = await Client(http).GetLiveCategoriesAsync(CancellationToken.None);
            Assert.Equal(2, cats.Count);
            Assert.Equal("1", cats[0].Id);
            Assert.Equal("Sports", cats[1].Name);
            Assert.Equal("2", cats[1].Id);
        }

        [Fact]
        public async Task ParsesStreamsWithMixedTypesAndSkipsBrokenOnes()
        {
            var http = new FakeHttp().Route("get_live_streams", Fixture.Text("live_streams.json"));
            var streams = await Client(http).GetLiveStreamsAsync(CancellationToken.None);
            Assert.Equal(3, streams.Count);

            var a = streams[0];
            Assert.Equal("101", a.StreamId);
            Assert.Equal("news1.example", a.EpgChannelId);
            Assert.True(a.TvArchive);
            Assert.Equal(3, a.TvArchiveDuration);
            Assert.Equal("1", a.CategoryId);

            var b = streams[1];
            Assert.Equal(2, b.Num);
            Assert.Null(b.EpgChannelId);
            Assert.Null(b.StreamIcon);
            Assert.False(b.TvArchive);
            Assert.Equal("2", b.CategoryId);

            var c = streams[2];
            Assert.Equal(0, c.Num);
            Assert.True(c.TvArchive);
            Assert.Null(c.CategoryId);
        }

        [Fact]
        public async Task ParsesEpgWithBase64()
        {
            var http = new FakeHttp().Route("get_simple_data_table&stream_id=101", Fixture.Text("simple_data_table.json"));
            var epg = await Client(http).GetSimpleDataTableAsync("101", null, CancellationToken.None);
            Assert.Equal(2, epg.Count);
            Assert.Equal("News at Six", epg[0].Title);
            Assert.Equal("Daily news", epg[0].Description);
            Assert.Equal(new DateTime(2023, 11, 14, 17, 0, 0, DateTimeKind.Utc), epg[0].StartUtc);
            Assert.True(epg[0].HasArchive);
            Assert.Equal("Plain Title", epg[1].Title);
            Assert.False(epg[1].HasArchive);
        }

        [Theory]
        [InlineData("{}")]
        [InlineData("{\"epg_listings\":null}")]
        [InlineData("{\"epg_listings\":[]}")]
        [InlineData("[]")]
        [InlineData("{\"epg_listings\":\"nope\"}")]
        public void EpgToleratesMissingOrOddListings(string json)
        {
            Assert.Empty(XtreamClient.ParseEpgListings(JsonLite.Parse(json), null));
        }

        [Fact]
        public void EpgListingsAsObjectIsAccepted()
        {
            var json = "{\"epg_listings\":{\"a\":{\"title\":\"VGl0bGU=\",\"start_timestamp\":100,\"stop_timestamp\":200}}}";
            var list = XtreamClient.ParseEpgListings(JsonLite.Parse(json), null);
            Assert.Single(list);
            Assert.Equal("Title", list[0].Title);
        }

        [Fact]
        public void EpgFallsBackToLocalStringsInServerTimezone()
        {
            var info = new XtreamServerInfo { Timezone = "Europe/Berlin" };
            var json = "{\"epg_listings\":[{\"title\":\"x\",\"start\":\"2023-11-14 18:00:00\",\"end\":\"2023-11-14 19:00:00\"}]}";
            var e = XtreamClient.ParseEpgListings(JsonLite.Parse(json), info).Single();
            Assert.Equal(new DateTime(2023, 11, 14, 17, 0, 0, DateTimeKind.Utc), e.StartUtc);
        }

        [Theory]
        [InlineData("News", "News")]
        [InlineData("Test", "Test")]
        [InlineData("Hello World", "Hello World")]
        [InlineData("", "")]
        [InlineData("!!!!", "!!!!")]
        [InlineData("VGl0bGU=", "Title")]
        [InlineData("w6RiYw==", "\u00e4bc")]
        public void Base64DecodingFallsBackToPlainText(string input, string expected)
        {
            Assert.Equal(expected, XtreamClient.DecodeMaybeBase64(input));
        }

        [Fact]
        public async Task HttpFailureBecomesSanitisedXtreamException()
        {
            var ex = await Assert.ThrowsAsync<XtreamException>(() => Client(new FakeHttp()).GetAccountAsync(CancellationToken.None));
            Assert.DoesNotContain("p%20w", ex.Message);
            Assert.DoesNotContain("p w", ex.Message);
            Assert.Contains("password=***", ex.Message);
        }

        [Fact]
        public async Task InvalidJsonBecomesXtreamException()
        {
            var http = new FakeHttp().Route("player_api.php", "<html>blocked</html>");
            var ex = await Assert.ThrowsAsync<XtreamException>(() => Client(http).GetAccountAsync(CancellationToken.None));
            Assert.Contains("invalid JSON", ex.Message);
        }
    }
}
