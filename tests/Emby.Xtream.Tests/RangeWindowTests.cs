using System;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Emby.Xtream.Catchup;
using Xunit;

namespace Emby.Xtream.Tests
{
    public class RangeWindowTests
    {
        private static byte[] Bytes(int from, int count) => Enumerable.Range(from, count).Select(i => (byte)i).ToArray();

        [Fact]
        public void FindsTheWindowContainingAPositionPerKeyAndEvictsTheOldest()
        {
            var cache = new RangeWindowCache(maxWindows: 2);
            cache.Add("a", 1000, new byte[100]);
            cache.Add("b", 1000, new byte[100]);

            byte[] data; long start;
            Assert.True(cache.TryGet("a", 1050, out data, out start));
            Assert.Equal(1000, start);
            Assert.False(cache.TryGet("a", 1100, out data, out start)); // one past the end
            Assert.False(cache.TryGet("c", 1050, out data, out start));

            cache.Add("a", 5000, new byte[10]); // "b" is now the least recently used and goes
            Assert.False(cache.TryGet("b", 1050, out data, out start));
            Assert.True(cache.TryGet("a", 1001, out data, out start));
            Assert.True(cache.TryGet("a", 5009, out data, out start));
        }

        [Fact]
        public void AReaderThatStopsInsideTheWindowNeverOpensTheRest()
        {
            int opened = 0;
            using (var s = new WindowThenStream(Bytes(0, 100), 10, 50, 60, (from, ct) => { opened++; return Task.FromResult<Stream>(new MemoryStream()); }))
            {
                var buffer = new byte[20];
                Assert.Equal(20, s.Read(buffer, 0, 20));
                Assert.Equal(Bytes(10, 20), buffer);
            }
            Assert.Equal(0, opened);
        }

        [Fact]
        public void ContinuesFromTheProviderAtTheFirstByteAfterTheWindow()
        {
            long openedAt = -1;
            var s = new WindowThenStream(Bytes(0, 100), 90, 10, 1100, (from, ct) =>
            {
                openedAt = from;
                return Task.FromResult<Stream>(new MemoryStream(Bytes(100, 5)));
            });
            var output = new MemoryStream();
            s.CopyTo(output, 7);
            Assert.Equal(1100, openedAt);
            Assert.Equal(Bytes(90, 15), output.ToArray());
        }

        [Fact]
        public void EndsAtTheWindowWhenNothingFollows()
        {
            var s = new WindowThenStream(Bytes(0, 10), 0, 10, null, (from, ct) => throw new InvalidOperationException("must not open"));
            var output = new MemoryStream();
            s.CopyTo(output);
            Assert.Equal(10, output.Length);
        }
    }
}
