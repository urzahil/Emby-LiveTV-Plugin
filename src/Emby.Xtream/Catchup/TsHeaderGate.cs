using System;
using System.IO;

namespace Emby.Xtream.Catchup
{
    /// <summary>
    /// Drops the start of an MPEG-TS stream up to the first video access unit that carries the codec setup headers
    /// (H.264 SPS, HEVC SPS). Provider timeshift streams start at an arbitrary byte, and some only repeat those headers
    /// every several seconds; ffmpeg gives up after reading 5 MB without them ("unspecified size", "dimensions not set")
    /// and Emby cannot raise that limit. The output starts with the last PAT and PMT seen, then the packets from the start
    /// of that access unit on, then everything unchanged.
    /// <para>The gate opens without waiting when the stream has no H.264/HEVC video, and after <c>maxSkipBytes</c> in any case,
    /// so a stream it does not understand is delayed, never lost.</para>
    /// </summary>
    public sealed class TsHeaderGate
    {
        public const int PacketSize = 188;
        private const byte Sync = 0x47;
        private const int MaxScanBytes = 8192;
        private const int MaxCandidateBytes = 8 * 1024 * 1024;

        private readonly long _maxSkipBytes;
        private readonly byte[] _partial = new byte[PacketSize];
        private int _partialCount;

        private byte[] _pat;
        private byte[] _pmt;
        private int _pmtPid = -1;
        private int _videoPid = -1;
        private bool _isHevc;

        private MemoryStream _candidate;
        private readonly byte[] _scan = new byte[MaxScanBytes];
        private int _scanCount;
        private bool _scanDone;

        public TsHeaderGate(long maxSkipBytes = 64L * 1024 * 1024)
        {
            _maxSkipBytes = maxSkipBytes;
        }

        /// <summary>True once output passes straight through.</summary>
        public bool IsOpen { get; private set; }

        /// <summary>Why the gate opened: "headers", "no-h264-or-hevc", "limit" or "end".</summary>
        public string OpenReason { get; private set; }

        /// <summary>Input bytes dropped before the gate opened.</summary>
        public long SkippedBytes { get; private set; }

        /// <summary>Feeds input bytes; whatever may be played is written to <paramref name="output"/>.</summary>
        public void Write(byte[] buffer, int offset, int count, Stream output)
        {
            if (IsOpen)
            {
                output.Write(buffer, offset, count);
                return;
            }

            int end = offset + count;
            while (offset < end)
            {
                if (_partialCount == 0 && buffer[offset] != Sync)
                {
                    // resync: skip bytes until a packet starts
                    offset++;
                    SkippedBytes++;
                    continue;
                }

                int take = Math.Min(PacketSize - _partialCount, end - offset);
                Buffer.BlockCopy(buffer, offset, _partial, _partialCount, take);
                _partialCount += take;
                offset += take;
                if (_partialCount < PacketSize) break;

                _partialCount = 0;
                OnPacket(_partial, output);

                if (IsOpen)
                {
                    if (offset < end) output.Write(buffer, offset, end - offset);
                    return;
                }
                if (SkippedBytes > _maxSkipBytes) Open("limit", output);
            }
        }

        /// <summary>Call at end of input: a stream that ended before the gate opened is still handed over.</summary>
        public void Complete(Stream output)
        {
            if (!IsOpen) Open("end", output);
        }

        private void OnPacket(byte[] p, Stream output)
        {
            int pid = ((p[1] & 0x1F) << 8) | p[2];
            bool unitStart = (p[1] & 0x40) != 0;
            int adaptation = (p[3] >> 4) & 0x3;
            int payload = 4;
            if (adaptation == 2 || adaptation == 3) payload = 5 + p[4];
            bool hasPayload = (adaptation == 1 || adaptation == 3) && payload < PacketSize;

            if (pid == 0)
            {
                _pat = (byte[])p.Clone();
                if (hasPayload && unitStart) ParsePat(p, payload);
                Drop();
                return;
            }
            if (pid == _pmtPid)
            {
                _pmt = (byte[])p.Clone();
                if (hasPayload && unitStart && _videoPid < 0)
                {
                    ParsePmt(p, payload);
                    if (_videoPid < 0) { Open("no-h264-or-hevc", output); return; }
                }
                Drop();
                return;
            }

            if (pid == _videoPid && unitStart && hasPayload)
            {
                // a new access unit starts: forget the previous candidate, it had no headers
                if (_candidate != null) SkippedBytes += _candidate.Length;
                _candidate = new MemoryStream();
                _scanCount = 0;
                _scanDone = false;
                _candidate.Write(p, 0, PacketSize);
                ScanPes(p, payload, true);
            }
            else if (_candidate != null)
            {
                _candidate.Write(p, 0, PacketSize);
                if (pid == _videoPid && hasPayload) ScanPes(p, payload, false);
                if (_candidate.Length > MaxCandidateBytes)
                {
                    SkippedBytes += _candidate.Length;
                    _candidate = null;
                    return;
                }
            }
            else
            {
                Drop();
                return;
            }

            if (!_scanDone && ContainsSps()) Open("headers", output);
        }

        private void Drop() => SkippedBytes += PacketSize;

        private void ScanPes(byte[] p, int payload, bool pesStart)
        {
            if (_scanDone) return;
            int start = payload;
            if (pesStart)
            {
                // PES header: 00 00 01 stream_id len(2) flags(2) header_data_length(1)
                if (payload + 9 > PacketSize || p[payload] != 0 || p[payload + 1] != 0 || p[payload + 2] != 1) { _scanDone = true; return; }
                start = payload + 9 + p[payload + 8];
                if (start >= PacketSize) return;
            }
            int n = Math.Min(PacketSize - start, MaxScanBytes - _scanCount);
            Buffer.BlockCopy(p, start, _scan, _scanCount, n);
            _scanCount += n;
        }

        private bool ContainsSps()
        {
            for (int i = 0; i + 3 < _scanCount; i++)
            {
                if (_scan[i] != 0 || _scan[i + 1] != 0 || _scan[i + 2] != 1) continue;
                byte h = _scan[i + 3];
                if (_isHevc ? ((h >> 1) & 0x3F) == 33 : (h & 0x1F) == 7) return true;
            }
            if (_scanCount >= MaxScanBytes) _scanDone = true;
            return false;
        }

        private void Open(string reason, Stream output)
        {
            IsOpen = true;
            OpenReason = reason;
            if (_pat != null) output.Write(_pat, 0, PacketSize);
            if (_pmt != null) output.Write(_pmt, 0, PacketSize);
            if (_candidate != null)
            {
                _candidate.Position = 0;
                _candidate.CopyTo(output);
                _candidate = null;
            }
            if (_partialCount > 0) output.Write(_partial, 0, _partialCount);
            _partialCount = 0;
        }

        private void ParsePat(byte[] p, int payload)
        {
            int t = payload + 1 + p[payload]; // skip pointer_field
            if (t + 8 > PacketSize || p[t] != 0x00) return;
            int sectionLength = ((p[t + 1] & 0x0F) << 8) | p[t + 2];
            int end = Math.Min(t + 3 + sectionLength - 4, PacketSize);
            for (int i = t + 8; i + 4 <= end; i += 4)
            {
                int program = (p[i] << 8) | p[i + 1];
                if (program == 0) continue; // network PID
                _pmtPid = ((p[i + 2] & 0x1F) << 8) | p[i + 3];
                return;
            }
        }

        private void ParsePmt(byte[] p, int payload)
        {
            int t = payload + 1 + p[payload];
            if (t + 12 > PacketSize || p[t] != 0x02) return;
            int sectionLength = ((p[t + 1] & 0x0F) << 8) | p[t + 2];
            int end = Math.Min(t + 3 + sectionLength - 4, PacketSize);
            int programInfoLength = ((p[t + 10] & 0x0F) << 8) | p[t + 11];
            for (int i = t + 12 + programInfoLength; i + 5 <= end;)
            {
                byte streamType = p[i];
                int pid = ((p[i + 1] & 0x1F) << 8) | p[i + 2];
                int esInfoLength = ((p[i + 3] & 0x0F) << 8) | p[i + 4];
                if (streamType == 0x1B || streamType == 0x24)
                {
                    _videoPid = pid;
                    _isHevc = streamType == 0x24;
                    return;
                }
                i += 5 + esInfoLength;
            }
        }
    }
}
