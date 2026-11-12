using System;
using Emby.Xtream.Xtream;
using Xunit;

namespace Emby.Xtream.Tests
{
    public class XtreamUrlBuilderTests
    {
        private static XtreamUrlBuilder B(string url = "http://host:8080/", string u = "user", string p = "pass") =>
            new XtreamUrlBuilder(new XtreamCredentials(url, u, p));

        [Fact]
        public void ApiUrls()
        {
            var b = B();
            Assert.Equal("http://host:8080/player_api.php?username=user&password=pass", b.PlayerApi());
            Assert.Equal("http://host:8080/player_api.php?username=user&password=pass&action=get_live_categories", b.LiveCategories());
            Assert.Equal("http://host:8080/player_api.php?username=user&password=pass&action=get_live_streams", b.LiveStreams());
            Assert.EndsWith("&action=get_live_streams&category_id=7", b.LiveStreams("7"));
            Assert.EndsWith("&action=get_simple_data_table&stream_id=12", b.SimpleDataTable("12"));
            Assert.EndsWith("&action=get_short_epg&stream_id=12&limit=4", b.ShortEpg("12", 4));
            Assert.Equal("http://host:8080/xmltv.php?username=user&password=pass", b.Xmltv());
        }

        [Fact]
        public void LiveUrl()
        {
            Assert.Equal("http://host:8080/live/user/pass/55.ts", B().Live("55"));
            Assert.Equal("http://host:8080/live/user/pass/55.m3u8", B().Live("55", "m3u8"));
            Assert.Equal("http://host:8080/live/user/pass/55.ts", B().Live("55", ".ts"));
        }

        [Fact]
        public void CredentialsAreUrlEncoded()
        {
            var b = B(u: "us er", p: "p&ss/w?rd#1");
            string url = b.PlayerApi();
            Assert.Contains("username=us%20er", url);
            Assert.Contains("password=p%26ss%2Fw%3Frd%231", url);
            Assert.Equal("http://host:8080/live/us%20er/p%26ss%2Fw%3Frd%231/5.ts", b.Live("5"));
        }

        [Fact]
        public void TimeshiftQueryStyle()
        {
            var url = B().Timeshift("55", new DateTime(2023, 11, 14, 18, 5, 0), 90);
            Assert.Equal("http://host:8080/streaming/timeshift.php?username=user&password=pass&stream=55&start=2023-11-14:18-05&duration=90", url);
        }

        [Fact]
        public void TimeshiftPathStyle()
        {
            var url = B().Timeshift("55", new DateTime(2023, 1, 2, 3, 4, 0), 30, "path");
            Assert.Equal("http://host:8080/timeshift/user/pass/30/2023-01-02:03-04/55.ts", url);
        }

        [Fact]
        public void TimeshiftDurationIsAtLeastOneMinute()
        {
            Assert.Contains("duration=1", B().Timeshift("1", DateTime.UtcNow, 0));
        }

        [Theory]
        [InlineData("http://host:8080/", "http://host:8080")]
        [InlineData("host:8080", "http://host:8080")]
        [InlineData("  https://host/  ", "https://host")]
        [InlineData("http://host:8080/player_api.php?username=a&password=b", "http://host:8080")]
        [InlineData("http://host/get.php?username=a", "http://host")]
        [InlineData("http://host/panel/", "http://host/panel")]
        [InlineData("http://host/live/a/b/1.ts", "http://host")]
        public void NormalizesBaseUrl(string input, string expected)
        {
            Assert.Equal(expected, XtreamCredentials.NormalizeBaseUrl(input));
        }

        [Fact]
        public void SubPathPanelsKeepTheirPath()
        {
            var b = B("http://host/panel/");
            Assert.StartsWith("http://host/panel/player_api.php", b.PlayerApi());
        }

        [Theory]
        [InlineData("http://h:8080/player_api.php?username=u&password=SECRET&action=x", "SECRET")]
        [InlineData("http://h/xmltv.php?password=SECRET&username=u", "SECRET")]
        [InlineData("http://h/live/USERX/SECRET/1.ts", "SECRET")]
        [InlineData("http://h/live/USERX/SECRET/1.ts", "USERX")]
        [InlineData("http://h/timeshift/USERX/SECRET/60/2023-01-01:10-00/1.ts", "SECRET")]
        [InlineData("http://h/streaming/timeshift.php?username=USERX&password=SECRET&stream=1", "USERX")]
        [InlineData("http://h/movie/USERX/SECRET/9.mkv", "SECRET")]
        public void SanitizeRemovesSecrets(string url, string secret)
        {
            string s = XtreamUrlBuilder.Sanitize(url);
            Assert.DoesNotContain(secret, s);
            Assert.StartsWith("http://h", s);
        }

        [Fact]
        public void SanitizeKeepsNonSecretParts()
        {
            Assert.Equal("http://h:8080/player_api.php?username=***&password=***&action=get_live_streams",
                XtreamUrlBuilder.Sanitize("http://h:8080/player_api.php?username=u&password=p&action=get_live_streams"));
            Assert.Equal("http://h/live/***/***/1.ts", XtreamUrlBuilder.Sanitize("http://h/live/u/p/1.ts"));
            Assert.Null(XtreamUrlBuilder.Sanitize(null));
        }

        [Fact]
        public void SanitizeTextMasksUrlsInsideMessages()
        {
            string s = XtreamUrlBuilder.SanitizeText("Error 404 for http://h/player_api.php?username=u&password=SECRET&x=1 (live http://h/live/USERX/SECRET/3.ts)");
            Assert.DoesNotContain("SECRET", s);
            Assert.DoesNotContain("USERX", s);
        }
    }

    public class XtreamCredentialsTests
    {
        [Fact]
        public void ParsesGetPhpUrl()
        {
            var c = XtreamCredentials.ParseM3uUrl("http://prov.example:8080/get.php?username=bob&password=s%40cret&type=m3u_plus&output=ts");
            Assert.NotNull(c);
            Assert.Equal("http://prov.example:8080", c.BaseUrl);
            Assert.Equal("bob", c.Username);
            Assert.Equal("s@cret", c.Password);
            Assert.True(c.IsComplete);
        }

        [Fact]
        public void ParsesPlayerApiAndHttps()
        {
            var c = XtreamCredentials.ParseM3uUrl("https://p.example/player_api.php?password=pw&username=u");
            Assert.Equal("https://p.example", c.BaseUrl);
            Assert.Equal("u", c.Username);
            Assert.Equal("pw", c.Password);
        }

        [Theory]
        [InlineData(null)]
        [InlineData("")]
        [InlineData("http://host:8080")]
        [InlineData("http://host/get.php?type=m3u")]
        [InlineData("http://host/get.php?username=u")]
        public void ReturnsNullWithoutCredentials(string url)
        {
            Assert.Null(XtreamCredentials.ParseM3uUrl(url));
        }

        [Fact]
        public void IsCompleteRequiresAllParts()
        {
            Assert.False(new XtreamCredentials("http://h", "u", "").IsComplete);
            Assert.False(new XtreamCredentials("", "u", "p").IsComplete);
            Assert.True(new XtreamCredentials("h", "u", "p").IsComplete);
        }
    }
}
