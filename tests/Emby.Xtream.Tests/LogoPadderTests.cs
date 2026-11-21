using System;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Emby.Xtream.Catchup;
using Emby.Xtream.Xtream;
using Xunit;

namespace Emby.Xtream.Tests
{
    /// <summary>
    /// Fixtures in TestData/logos were made with ffmpeg: a 64×10 test pattern (left 8 columns transparent where the format
    /// has alpha) saved in each PNG colour format, plus ffmpeg's own decoding of each to raw RGBA (*.rgba) as the reference.
    /// </summary>
    public class LogoPadderTests
    {
        private static byte[] Logo(string name) => File.ReadAllBytes(Path.Combine(AppContext.BaseDirectory, "TestData", "logos", name));

        [Theory]
        [InlineData("rgba", 0)]
        [InlineData("rgb24", 0)]
        [InlineData("pal8", 0)]
        [InlineData("gray", 0)]
        [InlineData("ya8", 0)]
        [InlineData("monob", 0)]
        [InlineData("rgb48be", 1)]   // 16-bit: we keep the high byte, ffmpeg rounds
        [InlineData("rgba64be", 1)]
        [InlineData("gray16be", 1)]
        public void DecodesEveryPngColourFormatLikeFfmpeg(string format, int tolerance)
        {
            int width, height;
            byte[] rgba = LogoPadder.DecodeToRgba(Logo("wide_" + format + ".png"), out width, out height);
            byte[] expected = Logo("wide_" + format + ".rgba");

            Assert.NotNull(rgba);
            Assert.Equal(64, width);
            Assert.Equal(10, height);
            Assert.Equal(expected.Length, rgba.Length);
            int worst = Enumerable.Range(0, rgba.Length).Max(i => Math.Abs(rgba[i] - expected[i]));
            Assert.True(worst <= tolerance, "largest difference " + worst);
        }

        [Fact]
        public void PadsAWideLogoTo16By9WithTheLogoCentred()
        {
            byte[] original = Logo("wide_rgba.png");
            byte[] padded;
            Assert.Equal(LogoPadder.Verdict.UsePadded, LogoPadder.Process(original, out padded));

            int w, h;
            Assert.True(LogoPadder.TryPngSize(padded, out w, out h));
            Assert.Equal(64, w);
            Assert.Equal(36, h); // ceil(64 * 9 / 16)

            int ow, oh;
            byte[] source = LogoPadder.DecodeToRgba(original, out ow, out oh);
            byte[] result = LogoPadder.DecodeToRgba(padded, out w, out h);
            int top = (36 - 10) / 2;
            for (int y = 0; y < 36; y++)
                for (int x = 0; x < 64; x++)
                    for (int c = 0; c < 4; c++)
                    {
                        byte actual = result[(y * 64 + x) * 4 + c];
                        byte want = y >= top && y < top + 10 ? source[((y - top) * 64 + x) * 4 + c] : (byte)0;
                        Assert.Equal(want, actual);
                    }
        }

        [Theory]
        [InlineData(512, 77, true, 512, 288)]  // the logo that crashed the Android TV app
        [InlineData(1920, 1080, false, 1920, 1080)]
        [InlineData(300, 300, false, 300, 300)]
        [InlineData(100, 300, true, 150, 300)]
        [InlineData(200, 400, false, 200, 400)]
        public void ShapeLimits(int width, int height, bool pad, int targetWidth, int targetHeight)
        {
            int tw, th;
            Assert.Equal(pad, LogoPadder.NeedsPadding(width, height, out tw, out th));
            Assert.Equal(targetWidth, tw);
            Assert.Equal(targetHeight, th);
        }

        [Fact]
        public void JpegsAreMeasuredKeptWhenFineAndDroppedWhenTooWide()
        {
            int w, h;
            Assert.True(LogoPadder.TryJpegSize(Logo("ok.jpg"), out w, out h));
            Assert.Equal(32, w);
            Assert.Equal(20, h);

            byte[] padded;
            Assert.Equal(LogoPadder.Verdict.KeepOriginal, LogoPadder.Process(Logo("ok.jpg"), out padded));
            Assert.Equal(LogoPadder.Verdict.Drop, LogoPadder.Process(Logo("wide.jpg"), out padded));
        }

        [Fact]
        public void UnknownOrBrokenDataIsKeptOrDroppedButNeverThrows()
        {
            byte[] padded;
            Assert.Equal(LogoPadder.Verdict.KeepOriginal, LogoPadder.Process(new byte[] { 1, 2, 3 }, out padded));
            Assert.Equal(LogoPadder.Verdict.KeepOriginal, LogoPadder.Process(new byte[0], out padded));

            byte[] truncated = Logo("wide_rgba.png").Take(60).ToArray();
            Assert.Equal(LogoPadder.Verdict.Drop, LogoPadder.Process(truncated, out padded));
        }
    }

    public class CatchupLogoCacheTests
    {
        private sealed class BytesHttp : IXtreamHttp
        {
            public int Calls;
            private readonly byte[] _body;
            public BytesHttp(byte[] body) { _body = body; }

            public Task<Stream> GetStreamAsync(string url, CancellationToken ct)
            {
                Calls++;
                if (_body == null) throw new InvalidOperationException("HTTP 500");
                return Task.FromResult<Stream>(new MemoryStream(_body));
            }
        }

        private static byte[] Logo(string name) => File.ReadAllBytes(Path.Combine(AppContext.BaseDirectory, "TestData", "logos", name));

        [Fact]
        public async Task WideLogosBecomeAPaddedFileOthersKeepTheirUrl()
        {
            string dir = Path.Combine(Path.GetTempPath(), "xtream-logos-" + Guid.NewGuid().ToString("N"));
            try
            {
                var cache = new CatchupLogoCache(dir);
                var wide = new BytesHttp(Logo("wide_rgba.png"));

                string path = await cache.ResolveAsync("http://logos/wide.png", wide, CancellationToken.None);
                Assert.StartsWith(dir, path);
                Assert.True(File.Exists(path));
                Assert.Equal(path, await cache.ResolveAsync("http://logos/wide.png", wide, CancellationToken.None));
                Assert.Equal(1, wide.Calls);

                // a new cache (server restart) reuses the file without downloading
                var again = new BytesHttp(null);
                Assert.Equal(path, await new CatchupLogoCache(dir).ResolveAsync("http://logos/wide.png", again, CancellationToken.None));
                Assert.Equal(0, again.Calls);

                Assert.Equal("http://logos/ok.jpg", await cache.ResolveAsync("http://logos/ok.jpg", new BytesHttp(Logo("ok.jpg")), CancellationToken.None));
                Assert.Null(await cache.ResolveAsync("http://logos/wide.jpg", new BytesHttp(Logo("wide.jpg")), CancellationToken.None));
                Assert.Null(await cache.ResolveAsync("  ", wide, CancellationToken.None));

                // download failure keeps the URL and retries next time
                var failing = new BytesHttp(null);
                Assert.Equal("http://logos/down.png", await cache.ResolveAsync("http://logos/down.png", failing, CancellationToken.None));
                await cache.ResolveAsync("http://logos/down.png", failing, CancellationToken.None);
                Assert.Equal(2, failing.Calls);
            }
            finally
            {
                if (Directory.Exists(dir)) Directory.Delete(dir, true);
            }
        }
    }
}
