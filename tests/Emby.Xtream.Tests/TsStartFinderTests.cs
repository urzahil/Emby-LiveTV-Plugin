using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Emby.Xtream.Catchup;
using Xunit;

namespace Emby.Xtream.Tests
{
    /// <summary>Builds minimal MPEG-TS packets: PAT -> PMT (pid 0x1000) -> video 0x100 + audio 0x101.</summary>
    internal static class Ts
    {
        public const int PmtPid = 0x1000, VideoPid = 0x100, AudioPid = 0x101;

        public static byte[] Packet(int pid, bool unitStart, byte[] payload, byte marker = 0)
        {
            var p = Enumerable.Repeat((byte)0xFF, 188).ToArray();
            p[0] = 0x47;
            p[1] = (byte)((unitStart ? 0x40 : 0) | ((pid >> 8) & 0x1F));
            p[2] = (byte)(pid & 0xFF);
            p[3] = 0x10; // payload only
            Buffer.BlockCopy(payload, 0, p, 4, Math.Min(payload.Length, 184));
            if (marker != 0) p[187] = marker;
            return p;
        }

        public static byte[] Pat() => Packet(0, true, new byte[]
        {
            0x00, // pointer
            0x00, 0xB0, 0x0D, 0x00, 0x01, 0xC1, 0x00, 0x00,
            0x00, 0x01, (byte)(0xE0 | (PmtPid >> 8)), (byte)(PmtPid & 0xFF),
            0, 0, 0, 0 // crc (not checked)
        });

        public static byte[] Pmt(byte videoType = 0x1B) => Packet(PmtPid, true, new byte[]
        {
            0x00,
            0x02, 0xB0, 0x17, 0x00, 0x01, 0xC1, 0x00, 0x00,
            (byte)(0xE0 | (VideoPid >> 8)), (byte)(VideoPid & 0xFF), // PCR pid
            0xF0, 0x00, // program_info_length 0
            videoType, (byte)(0xE0 | (VideoPid >> 8)), (byte)(VideoPid & 0xFF), 0xF0, 0x00,
            0x0F, (byte)(0xE0 | (AudioPid >> 8)), (byte)(AudioPid & 0xFF), 0xF0, 0x00,
            0, 0, 0, 0
        });

        /// <summary>Start of a video PES whose first NAL unit has the given header byte.</summary>
        public static byte[] VideoStart(byte nalHeader, byte marker) => Packet(VideoPid, true, new byte[]
        {
            0x00, 0x00, 0x01, 0xE0, 0x00, 0x00, 0x80, 0x80, 0x05, 0x21, 0x00, 0x01, 0x00, 0x01, // PES header with PTS
            0x00, 0x00, 0x00, 0x01, 0x09, 0xF0, // access unit delimiter
            0x00, 0x00, 0x00, 0x01, nalHeader, 0x64, 0x00, 0x1F
        }, marker);

        public static byte[] VideoMore(byte marker) => Packet(VideoPid, false, new byte[] { 0x11, 0x22 }, marker);
        public static byte[] Audio(byte marker) => Packet(AudioPid, true, new byte[] { 0x00, 0x00, 0x01, 0xC0 }, marker);

        public static byte[] Concat(params byte[][] packets) => packets.SelectMany(p => p).ToArray();

        public static List<byte[]> Split(byte[] data)
        {
            var list = new List<byte[]>();
            for (int i = 0; i + 188 <= data.Length; i += 188) list.Add(data.Skip(i).Take(188).ToArray());
            return list;
        }

    }

    public class TsStartFinderTests
    {
        private const byte H264Slice = 0x41, H264Sps = 0x67, HevcSps = 0x42, HevcSlice = 0x02;

        private static TsStartFinder Find(byte[] input, int chunk = 1000, long maxScan = 64L * 1024 * 1024)
        {
            var finder = new TsStartFinder(maxScan);
            for (int i = 0; i < input.Length && !finder.IsDone; i += chunk) finder.Write(input, i, Math.Min(chunk, input.Length - i));
            finder.Complete();
            return finder;
        }

        [Theory]
        [InlineData(1)]
        [InlineData(100)]
        [InlineData(188)]
        [InlineData(65536)]
        public void StartsAtTheLastPatBeforeTheFirstAccessUnitWithSps(int chunk)
        {
            byte[] input = Ts.Concat(
                Ts.VideoMore(1),                 // 0: mid-PES garbage
                Ts.Pat(), Ts.Pmt(),              // 1, 2
                Ts.VideoStart(H264Slice, 2),     // 3: access unit without headers
                Ts.Audio(3), Ts.VideoMore(4),    // 4, 5
                Ts.Pat(), Ts.Pmt(),              // 6, 7  <- start here
                Ts.VideoStart(H264Sps, 5),       // 8: keyframe with SPS
                Ts.Audio(6));

            var finder = Find(input, chunk);
            Assert.Equal("headers", finder.Reason);
            Assert.Equal(6 * 188, finder.Offset);
        }

        [Fact]
        public void AnSpsWithoutAPatAndPmtBeforeItIsNotAStart()
        {
            byte[] input = Ts.Concat(
                Ts.VideoStart(H264Sps, 1),       // 0: headers, but no tables yet
                Ts.Pat(), Ts.VideoStart(H264Sps, 2), // 1: PAT without PMT before the keyframe
                Ts.Pat(), Ts.Pmt(), Ts.VideoStart(H264Sps, 3)); // 3 <- start
            Assert.Equal(3 * 188, Find(input).Offset);
        }

        [Fact]
        public void FindsHevcSps()
        {
            byte[] input = Ts.Concat(Ts.Pat(), Ts.Pmt(0x24), Ts.VideoStart(HevcSlice, 1), Ts.Pat(), Ts.Pmt(0x24), Ts.VideoStart(HevcSps, 2));
            Assert.Equal(3 * 188, Find(input).Offset);
        }

        [Fact]
        public void StreamsWithoutH264OrHevcStartAtTheFirstPat()
        {
            byte[] input = Ts.Concat(Ts.Audio(1), Ts.Pat(), Ts.Pmt(0x02), Ts.VideoStart(H264Slice, 2));
            var finder = Find(input);
            Assert.Equal("no-h264-or-hevc", finder.Reason);
            Assert.Equal(188, finder.Offset);
        }

        [Fact]
        public void GivesUpAtTheScanLimitAndAtTheEnd()
        {
            var parts = new List<byte[]> { Ts.Pat(), Ts.Pmt() };
            for (int i = 0; i < 50; i++) parts.Add(Ts.VideoStart(H264Slice, (byte)(i + 1)));
            byte[] input = Ts.Concat(parts.ToArray());

            var limited = Find(input, 188, maxScan: 10 * 188);
            Assert.Equal("limit", limited.Reason);
            Assert.Equal(0, limited.Offset);

            var ended = Find(input);
            Assert.Equal("end", ended.Reason);
            Assert.Equal(0, ended.Offset);
        }

        [Fact]
        public void OffsetsCountBytesBeforeTheFirstSyncByte()
        {
            byte[] input = Ts.Concat(new byte[] { 1, 2, 3 }, Ts.Pat(), Ts.Pmt(), Ts.VideoStart(H264Sps, 9));
            Assert.Equal(3, Find(input).Offset);
        }
    }

    public class ByteRangeTests
    {
        [Theory]
        [InlineData("bytes=0-", 0L, null)]
        [InlineData("bytes=100-", 100L, null)]
        [InlineData("bytes=100-199", 100L, 199L)]
        [InlineData(" BYTES = 5 - 6 ", null, null)]
        public void Parses(string header, long? from, long? to)
        {
            var r = ByteRange.Parse(header);
            if (from == null) { Assert.Null(r); return; }
            Assert.Equal(from.Value, r.From);
            Assert.Equal(to, r.To);
        }

        [Theory]
        [InlineData(null)]
        [InlineData("")]
        [InlineData("bytes=-500")]
        [InlineData("bytes=0-1,5-6")]
        [InlineData("bytes=9-3")]
        [InlineData("items=0-1")]
        [InlineData("bytes=a-b")]
        public void RejectsWhatItDoesNotSupport(string header) => Assert.Null(ByteRange.Parse(header));

        [Fact]
        public void ShiftsAndClampsToTheResource()
        {
            Assert.Equal("bytes=1100-1199", ByteRange.Parse("bytes=100-199").ToHeader(1000));
            Assert.Equal("bytes=1100-", ByteRange.Parse("bytes=100-").ToHeader(1000));
            Assert.Equal(499, ByteRange.Parse("bytes=100-").LastByte(500));
            Assert.Equal(199, ByteRange.Parse("bytes=100-199").LastByte(500));
            Assert.Equal(499, ByteRange.Parse("bytes=100-9999").LastByte(500));
        }
    }

    public class PrefixedStreamTests
    {
        [Fact]
        public void ReturnsThePrefixSliceThenTheInnerStream()
        {
            byte[] prefix = { 9, 9, 1, 2, 3, 9 };
            using (var s = new PrefixedStream(prefix, 2, 3, new MemoryStream(new byte[] { 4, 5 })))
            {
                var output = new MemoryStream();
                s.CopyTo(output, 2);
                Assert.Equal(new byte[] { 1, 2, 3, 4, 5 }, output.ToArray());
            }
        }
    }

    public class CatchupStreamRegistryTests
    {
        private sealed class Conn : IDisposable
        {
            public int Disposed;
            public void Dispose() => Disposed++;
        }

        [Fact]
        public void ANewConnectionForAProgrammeClosesThePreviousOneOnly()
        {
            var now = DateTime.UtcNow;
            string a = CatchupStreamRegistry.Register("http://p/active/a.ts", "UA", now);
            string b = CatchupStreamRegistry.Register("http://p/active/b.ts", "UA", now);
            var a1 = new Conn(); var a2 = new Conn(); var b1 = new Conn();

            CatchupStreamRegistry.ReplaceActive(a, a1);
            CatchupStreamRegistry.ReplaceActive(b, b1);
            CatchupStreamRegistry.ReplaceActive(a, null);   // a seek arrives: close a's old connection
            Assert.Equal(1, a1.Disposed);
            Assert.Equal(0, b1.Disposed);                   // other programmes are left alone

            CatchupStreamRegistry.ReplaceActive(a, a2);
            CatchupStreamRegistry.ReplaceActive(a, a2);     // the same connection again is not closed
            Assert.Equal(0, a2.Disposed);
            Assert.Equal(1, a1.Disposed);
        }

        [Fact]
        public void TokensAreRandomStableForAUrlAndExpire()
        {
            var now = new DateTime(2026, 11, 21, 8, 0, 0, DateTimeKind.Utc);
            string a = CatchupStreamRegistry.Register("http://p/timeshift/u/p/60/x/1.ts", "UA", now);
            string b = CatchupStreamRegistry.Register("http://p/timeshift/u/p/60/x/2.ts", "UA", now);
            Assert.Matches("^[0-9a-f]{32}$", a);
            Assert.NotEqual(a, b);
            Assert.Equal(a, CatchupStreamRegistry.Register("http://p/timeshift/u/p/60/x/1.ts", "UA", now.AddMinutes(1)));

            CatchupStreamRegistry.Entry entry;
            Assert.True(CatchupStreamRegistry.TryResolve(a, now.AddHours(11), out entry));
            Assert.Equal("http://p/timeshift/u/p/60/x/1.ts", entry.Url);
            Assert.False(CatchupStreamRegistry.TryResolve("0123456789abcdef0123456789abcdef", now, out entry));
            Assert.False(CatchupStreamRegistry.TryResolve(null, now, out entry));
            // b was last used at "now"; 13 h later it is gone
            Assert.False(CatchupStreamRegistry.TryResolve(b, now.AddHours(13), out entry));
        }
    }
}
