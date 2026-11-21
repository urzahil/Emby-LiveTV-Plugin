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

        public static byte[] Run(TsHeaderGate gate, byte[] input, int chunk = 1000)
        {
            var output = new MemoryStream();
            for (int i = 0; i < input.Length; i += chunk) gate.Write(input, i, Math.Min(chunk, input.Length - i), output);
            gate.Complete(output);
            return output.ToArray();
        }
    }

    public class TsHeaderGateTests
    {
        private const byte H264Slice = 0x41, H264Sps = 0x67, HevcSps = 0x42, HevcSlice = 0x02;

        [Theory]
        [InlineData(1)]
        [InlineData(100)]
        [InlineData(188)]
        [InlineData(65536)]
        public void StartsAtTheFirstAccessUnitWithSpsAndPrependsPatAndPmt(int chunk)
        {
            byte[] input = Ts.Concat(
                Ts.VideoMore(1),                 // mid-PES garbage before any table
                Ts.Pat(), Ts.Pmt(),
                Ts.VideoStart(H264Slice, 2),     // access unit without headers
                Ts.Audio(3), Ts.VideoMore(4),
                Ts.Pat(), Ts.Pmt(),
                Ts.VideoStart(H264Sps, 5),       // first keyframe with SPS
                Ts.Audio(6), Ts.VideoMore(7), Ts.VideoStart(H264Slice, 8));

            var gate = new TsHeaderGate();
            var packets = Ts.Split(Ts.Run(gate, input, chunk));

            Assert.True(gate.IsOpen);
            Assert.Equal("headers", gate.OpenReason);
            Assert.Equal(6, packets.Count);
            Assert.Equal(0, packets[0][2]);                        // PAT
            Assert.Equal(Ts.PmtPid & 0xFF, packets[1][2]);         // PMT
            Assert.Equal(new byte[] { 5, 6, 7, 8 }, packets.Skip(2).Select(p => p[187]).ToArray());
            // everything before packet 5 was dropped from the input (the PAT/PMT copies in front are re-sent, not passed)
            Assert.Equal(8 * 188, gate.SkippedBytes);
        }

        [Fact]
        public void FindsHevcSps()
        {
            byte[] input = Ts.Concat(Ts.Pat(), Ts.Pmt(0x24), Ts.VideoStart(HevcSlice, 1), Ts.VideoStart(HevcSps, 2), Ts.Audio(3));
            var packets = Ts.Split(Ts.Run(new TsHeaderGate(), input));
            Assert.Equal(new byte[] { 2, 3 }, packets.Skip(2).Select(p => p[187]).ToArray());
        }

        [Fact]
        public void PassesThroughWhenVideoIsNotH264OrHevc()
        {
            byte[] input = Ts.Concat(Ts.Pat(), Ts.Pmt(0x02), Ts.VideoStart(H264Slice, 1), Ts.Audio(2));
            var gate = new TsHeaderGate();
            var packets = Ts.Split(Ts.Run(gate, input));
            Assert.Equal("no-h264-or-hevc", gate.OpenReason);
            Assert.Equal(new byte[] { 1, 2 }, packets.Skip(2).Select(p => p[187]).ToArray());
        }

        [Fact]
        public void GivesUpAfterTheSkipLimitAndKeepsEverythingAfterIt()
        {
            var parts = new List<byte[]> { Ts.Pat(), Ts.Pmt() };
            for (int i = 0; i < 50; i++) parts.Add(Ts.VideoStart(H264Slice, (byte)(i + 1)));
            var gate = new TsHeaderGate(maxSkipBytes: 10 * 188);
            var packets = Ts.Split(Ts.Run(gate, Ts.Concat(parts.ToArray()), chunk: 188));

            Assert.Equal("limit", gate.OpenReason);
            byte[] markers = packets.Skip(2).Select(p => p[187]).ToArray();
            Assert.Equal(Enumerable.Range(markers[0], 51 - markers[0]).Select(i => (byte)i).ToArray(), markers);
        }

        [Fact]
        public void ResyncsOnAStreamThatDoesNotStartOnAPacketBoundary()
        {
            byte[] input = Ts.Concat(new byte[] { 1, 2, 3 }, Ts.Pat(), Ts.Pmt(), Ts.VideoStart(H264Sps, 9));
            var packets = Ts.Split(Ts.Run(new TsHeaderGate(), input));
            Assert.Equal(3, packets.Count);
            Assert.Equal(9, packets[2][187]);
        }

        [Fact]
        public void AStreamThatEndsBeforeTheGateOpensIsHandedOver()
        {
            byte[] input = Ts.Concat(Ts.Pat(), Ts.Pmt(), Ts.VideoStart(H264Slice, 4));
            var gate = new TsHeaderGate();
            var packets = Ts.Split(Ts.Run(gate, input));
            Assert.Equal("end", gate.OpenReason);
            Assert.Equal(4, packets[2][187]);
        }

        [Fact]
        public void GatedStreamReturnsTheFilteredBytes()
        {
            byte[] input = Ts.Concat(Ts.Pat(), Ts.Pmt(), Ts.VideoStart(H264Slice, 1), Ts.VideoStart(H264Sps, 2), Ts.VideoMore(3));
            TsHeaderGate opened = null;
            using (var stream = new GatedStream(new MemoryStream(input), new TsHeaderGate(), g => opened = g))
            {
                var output = new MemoryStream();
                stream.CopyTo(output, 7);
                Assert.Equal(new byte[] { 2, 3 }, Ts.Split(output.ToArray()).Skip(2).Select(p => p[187]).ToArray());
            }
            Assert.NotNull(opened);
        }
    }

    public class CatchupStreamRegistryTests
    {
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
