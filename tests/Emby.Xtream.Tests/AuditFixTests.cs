using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Threading;
using System.Threading.Tasks;
using Emby.Xtream.Catchup;
using Emby.Xtream.Xtream;
using Emby.Xtream.Xtream.Json;
using Xunit;

namespace Emby.Xtream.Tests
{
    public class TsSyncTests
    {
        private const byte H264Sps = 0x67;

        private static byte[] Stream(byte[] junk) => Ts.Concat(junk, Ts.Pat(), Ts.Pmt(), Ts.VideoStart(H264Sps, 1), Ts.Audio(2), Ts.Audio(3));

        [Theory]
        [InlineData(1)]
        [InlineData(188)]
        [InlineData(100000)]
        public void APayloadSyncByteBeforeTheRealBoundaryDoesNotMisalignThePackets(int chunk)
        {
            // junk ends mid-"packet" with a 0x47 whose following 188 bytes would swallow the real PAT
            var junk = new byte[150];
            junk[10] = 0x47;
            junk[149] = 0x47;
            byte[] input = Stream(junk);

            var finder = new TsStartFinder();
            for (int i = 0; i < input.Length; i += chunk) finder.Write(input, i, Math.Min(chunk, input.Length - i));
            finder.Complete();

            Assert.Equal("headers", finder.Reason);
            Assert.Equal(junk.Length, finder.Offset);
        }

        [Fact]
        public void ALonePayloadSyncByteWithPatLookingPidIsNotTreatedAsAPat()
        {
            // 0x47 0x00 0x00 ... would be a PAT header if it were taken for a packet start
            var junk = new byte[60];
            junk[5] = 0x47; junk[6] = 0x40; junk[7] = 0x00; junk[8] = 0x10;
            byte[] input = Stream(junk);
            var finder = new TsStartFinder();
            finder.Write(input, 0, input.Length);
            finder.Complete();
            Assert.Equal(junk.Length, finder.Offset);
        }

        [Fact]
        public void FindsTheStartAfterAGapInTheStream()
        {
            byte[] noise = Enumerable.Repeat((byte)0x11, 91).ToArray();
            byte[] input = Ts.Concat(Ts.Audio(1), Ts.Audio(2), Ts.Audio(3), noise, Ts.Pat(), Ts.Pmt(), Ts.VideoStart(H264Sps, 4), Ts.Audio(5), Ts.Audio(6));
            var finder = new TsStartFinder();
            finder.Write(input, 0, input.Length);
            finder.Complete();
            Assert.Equal(3 * 188 + noise.Length, finder.Offset);
        }
    }

    public class ByteRangeLimitTests
    {
        [Theory]
        [InlineData("bytes=9223372036854775807-")]
        [InlineData("bytes=0-9223372036854775807")]
        [InlineData("bytes=4611686018427387905-")]
        public void RejectsPositionsThatWouldOverflowWhenShifted(string header) => Assert.Null(ByteRange.Parse(header));

        [Fact]
        public void AcceptsLargeButSafePositions()
        {
            var r = ByteRange.Parse("bytes=4611686018427387903-");
            Assert.NotNull(r);
            Assert.True(long.Parse(r.ToHeader(1000).Substring(6).TrimEnd('-')) > 0);
        }
    }

    public class SanitizeSubPathTests
    {
        [Theory]
        [InlineData("http://h/panel/timeshift/USERX/SECRET/60/2026-01-01:10-00/1.ts")]
        [InlineData("http://h/panel/live/USERX/SECRET/1.ts")]
        [InlineData("http://h/a/b/movie/USERX/SECRET/9.mkv")]
        [InlineData("http://h/live/live/USERX/SECRET/1.ts")]
        public void MasksCredentialsBelowASubPath(string url)
        {
            string s = XtreamUrlBuilder.Sanitize(url);
            Assert.DoesNotContain("USERX", s);
            Assert.DoesNotContain("SECRET", s);
            Assert.StartsWith("http://h/", s);
        }

        [Fact]
        public void LeavesOtherUrlsAlone()
        {
            Assert.Equal("http://h/panel/player_api.php", XtreamUrlBuilder.Sanitize("http://h/panel/player_api.php"));
            Assert.Equal("http://h/live/***/***/1.ts", XtreamUrlBuilder.Sanitize("http://h/live/u/p/1.ts"));
        }
    }

    public class ChannelSignatureTests
    {
        [Fact]
        public void DiffersForTheSameCountsButOtherChannelsOrUrl()
        {
            string a = ShortHash.ChannelSignature("http://h", new[] { "a", "b" });
            Assert.Equal(a, ShortHash.ChannelSignature("http://h", new[] { "b", "a" }));
            Assert.NotEqual(a, ShortHash.ChannelSignature("http://h", new[] { "a", "c" }));
            Assert.NotEqual(a, ShortHash.ChannelSignature("http://other", new[] { "a", "b" }));
        }
    }

    public class JsonStreamLimitTests
    {
        private sealed class EndlessStream : Stream
        {
            public long Served;
            public override int Read(byte[] buffer, int offset, int count) { Array.Clear(buffer, offset, count); Served += count; return count; }
            public override bool CanRead => true;
            public override bool CanSeek => false;
            public override bool CanWrite => false;
            public override long Length => throw new NotSupportedException();
            public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
            public override void Flush() { }
            public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
            public override void SetLength(long value) => throw new NotSupportedException();
            public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        }

        [Fact]
        public void AnEndlessBodyFailsFastInsteadOfFillingTheMemory()
        {
            var endless = new EndlessStream();
            Assert.Throws<JsonLiteException>(() => JsonLite.Parse(endless, 1024 * 1024));
            Assert.InRange(endless.Served, 1, 4 * 1024 * 1024);
        }

        [Fact]
        public void NormalBodiesStillParse()
        {
            using (var s = new MemoryStream(System.Text.Encoding.UTF8.GetBytes("{\"a\":[1,2]}")))
                Assert.NotNull(JsonLite.Parse(s));
        }
    }

    public class LogoUrlPolicyTests
    {
        [Theory]
        [InlineData("http://127.0.0.1/x.png", false)]
        [InlineData("http://127.1.2.3:8096/x.png", false)]
        [InlineData("http://[::1]/x.png", false)]
        [InlineData("http://169.254.169.254/latest/meta-data", false)]
        [InlineData("http://[fe80::1]/x.png", false)]
        [InlineData("http://0.0.0.0/x.png", false)]
        [InlineData("http://[::ffff:127.0.0.1]/x.png", false)]
        [InlineData("http://localhost:8096/x.png", false)]
        [InlineData("http://LOCALHOST./x.png", false)]
        [InlineData("file:///etc/passwd", false)]
        [InlineData("ftp://203.0.113.5/x.png", false)]
        [InlineData("not a url", false)]
        [InlineData("http://user:pw@203.0.113.5/x.png", false)]
        [InlineData("http://203.0.113.5/x.png", true)]
        [InlineData("https://192.168.1.20/x.png", true)]
        [InlineData("http://[2001:db8::1]/x.png", true)]
        public async Task OnlyFetchesPublicOrLanHttpUrls(string url, bool allowed) =>
            Assert.Equal(allowed, await LogoUrlPolicy.IsAllowedAsync(url, CancellationToken.None));

        [Fact]
        public async Task ABlockedLogoIsNeverRequested()
        {
            string dir = Path.Combine(Path.GetTempPath(), "xtream-logos-" + Guid.NewGuid().ToString("N"));
            try
            {
                var http = new CountingHttp(new byte[] { 1 });
                Assert.Null(await new CatchupLogoCache(dir).ResolveAsync("http://169.254.169.254/latest", http, CancellationToken.None));
                Assert.Equal(0, http.Calls);
            }
            finally { if (Directory.Exists(dir)) Directory.Delete(dir, true); }
        }
    }

    internal sealed class CountingHttp : IXtreamHttp
    {
        private readonly byte[] _body;
        private readonly TimeSpan _delay;
        private int _calls, _running, _maxRunning;
        public CountingHttp(byte[] body, TimeSpan? delay = null) { _body = body; _delay = delay ?? TimeSpan.Zero; }
        public int Calls => _calls;
        public int MaxRunning => _maxRunning;

        public async Task<Stream> GetStreamAsync(string url, CancellationToken ct)
        {
            Interlocked.Increment(ref _calls);
            int running = Interlocked.Increment(ref _running);
            int seen;
            while ((seen = _maxRunning) < running && Interlocked.CompareExchange(ref _maxRunning, running, seen) != seen) { }
            try
            {
                if (_delay > TimeSpan.Zero) await Task.Delay(_delay, ct);
                return new MemoryStream(_body);
            }
            finally { Interlocked.Decrement(ref _running); }
        }
    }

    public class LogoCacheBehaviourTests
    {
        private static byte[] Logo(string name) => File.ReadAllBytes(Path.Combine(AppContext.BaseDirectory, "TestData", "logos", name));

        [Fact]
        public async Task KeepAndDropVerdictsSurviveARestartWithoutDownloading()
        {
            string dir = Path.Combine(Path.GetTempPath(), "xtream-logos-" + Guid.NewGuid().ToString("N"));
            try
            {
                var first = new CatchupLogoCache(dir);
                Assert.Equal("http://203.0.113.5/ok.jpg", await first.ResolveAsync("http://203.0.113.5/ok.jpg", new CountingHttp(Logo("ok.jpg")), CancellationToken.None));
                Assert.Null(await first.ResolveAsync("http://203.0.113.5/wide.jpg", new CountingHttp(Logo("wide.jpg")), CancellationToken.None));
                Assert.True(File.Exists(Path.Combine(dir, "verdicts.json")));

                var http = new CountingHttp(null);
                var restarted = new CatchupLogoCache(dir);
                Assert.Equal("http://203.0.113.5/ok.jpg", await restarted.ResolveAsync("http://203.0.113.5/ok.jpg", http, CancellationToken.None));
                Assert.Null(await restarted.ResolveAsync("http://203.0.113.5/wide.jpg", http, CancellationToken.None));
                Assert.Equal(0, http.Calls);
            }
            finally { if (Directory.Exists(dir)) Directory.Delete(dir, true); }
        }

        [Fact]
        public async Task ADamagedManifestIsIgnored()
        {
            string dir = Path.Combine(Path.GetTempPath(), "xtream-logos-" + Guid.NewGuid().ToString("N"));
            try
            {
                Directory.CreateDirectory(dir);
                File.WriteAllText(Path.Combine(dir, "verdicts.json"), "{ nope");
                var http = new CountingHttp(Logo("ok.jpg"));
                Assert.Equal("http://203.0.113.5/ok.jpg", await new CatchupLogoCache(dir).ResolveAsync("http://203.0.113.5/ok.jpg", http, CancellationToken.None));
                Assert.Equal(1, http.Calls);
            }
            finally { if (Directory.Exists(dir)) Directory.Delete(dir, true); }
        }

        [Fact]
        public async Task SameUrlIsFetchedOnceAndDownloadsRunInParallelUpToTheLimit()
        {
            string dir = Path.Combine(Path.GetTempPath(), "xtream-logos-" + Guid.NewGuid().ToString("N"));
            try
            {
                var cache = new CatchupLogoCache(dir);
                var http = new CountingHttp(Logo("ok.jpg"), TimeSpan.FromMilliseconds(50));

                var same = await Task.WhenAll(Enumerable.Range(0, 10).Select(_ => cache.ResolveAsync("http://203.0.113.5/same.jpg", http, CancellationToken.None)));
                Assert.All(same, r => Assert.Equal("http://203.0.113.5/same.jpg", r));
                Assert.Equal(1, http.Calls);

                var many = new CountingHttp(Logo("ok.jpg"), TimeSpan.FromMilliseconds(50));
                await Task.WhenAll(Enumerable.Range(0, 30).Select(i => cache.ResolveAsync("http://203.0.113.5/" + i + ".jpg", many, CancellationToken.None)));
                Assert.Equal(30, many.Calls);
                Assert.InRange(many.MaxRunning, 2, CatchupLogoCache.MaxParallelDownloads);
            }
            finally { if (Directory.Exists(dir)) Directory.Delete(dir, true); }
        }

        [Fact]
        public async Task ACancelledCallerDoesNotFailTheOthersWaitingForTheSameDownload()
        {
            string dir = Path.Combine(Path.GetTempPath(), "xtream-logos-" + Guid.NewGuid().ToString("N"));
            try
            {
                var cache = new CatchupLogoCache(dir);
                var http = new CountingHttp(Logo("ok.jpg"), TimeSpan.FromMilliseconds(200));
                var cts = new CancellationTokenSource();
                var first = cache.ResolveAsync("http://203.0.113.5/a.jpg", http, cts.Token);
                var second = cache.ResolveAsync("http://203.0.113.5/a.jpg", http, CancellationToken.None);
                cts.Cancel();
                await Assert.ThrowsAnyAsync<OperationCanceledException>(() => first);
                Assert.Equal("http://203.0.113.5/a.jpg", await second);
            }
            finally { if (Directory.Exists(dir)) Directory.Delete(dir, true); }
        }
    }

    public class RegistryLifecycleTests
    {
        private sealed class Conn : IDisposable
        {
            public int Disposed;
            public void Dispose() => Disposed++;
        }

        [Fact]
        public void AClosedConnectionIsForgottenAndExpiredTokensCloseTheirs()
        {
            var now = new DateTime(2026, 11, 21, 8, 0, 0, DateTimeKind.Utc);
            string token = CatchupStreamRegistry.Register("http://p/lifecycle/1.ts", "UA", now);
            var closed = new Conn();
            CatchupStreamRegistry.ReplaceActive(token, closed);
            CatchupStreamRegistry.ReleaseActive(token, closed);
            CatchupStreamRegistry.ReplaceActive(token, null);
            Assert.Equal(0, closed.Disposed); // released, so it was no longer the active one to close

            string other = CatchupStreamRegistry.Register("http://p/lifecycle/2.ts", "UA", now);
            var open = new Conn();
            CatchupStreamRegistry.ReplaceActive(other, open);
            CatchupStreamRegistry.Register("http://p/lifecycle/3.ts", "UA", now.AddHours(13)); // purges expired tokens
            Assert.Equal(1, open.Disposed);
        }
    }

    public class EpgCacheLockTests
    {
        [Fact]
        public async Task ClearDropsTheKeyLocksToo()
        {
            var cache = new EpgTableCache(2);
            for (int i = 0; i < 20; i++)
            {
                int n = i;
                await cache.GetAsync("k" + n, TimeSpan.FromMinutes(1), () => Task.FromResult(new List<XtreamEpgEntry>()), CancellationToken.None);
            }
            var field = typeof(EpgTableCache).GetField("_keyLocks", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
            var locks = (System.Collections.IDictionary)field.GetValue(cache);
            Assert.Equal(20, locks.Count);
            cache.Clear();
            Assert.Empty(locks);
        }
    }
}
