using System;

namespace Emby.Xtream.Catchup
{
    /// <summary>
    /// Finds where a catch-up stream should start: the last PAT before the first video access unit that carries the codec
    /// setup headers (H.264 SPS, HEVC SPS), with a PMT in between. Provider timeshift streams start at an arbitrary byte,
    /// and some only repeat those headers every several seconds; ffmpeg gives up after reading 5 MB without them
    /// ("unspecified size", "dimensions not set") and Emby cannot raise that limit.
    /// <para>The stream is served from that offset unchanged, so a byte position in the served stream is always the same
    /// provider position minus the offset, and byte-range seeking can be passed through to the provider.</para>
    /// <para>For streams without H.264/HEVC video the offset is the first PAT; when nothing suitable turns up within
    /// <c>maxScanBytes</c> or before the end, it is 0.</para>
    /// </summary>
    public sealed class TsStartFinder
    {
        public const int PacketSize = 188;
        private const byte Sync = 0x47;
        private const int MaxScanBytes = 8192;

        private readonly long _maxScanBytes;
        private readonly byte[] _packet = new byte[PacketSize];
        private int _packetCount;
        private long _packetStart;
        private long _position;

        private int _pmtPid = -1;
        private int _videoPid = -1;
        private bool _isHevc;

        private long _lastPat = -1;
        private bool _pmtSinceLastPat;
        private long _candidate = -1;
        private readonly byte[] _scan = new byte[MaxScanBytes];
        private int _scanCount;
        private bool _scanDone = true;

        public TsStartFinder(long maxScanBytes = 64L * 1024 * 1024)
        {
            _maxScanBytes = maxScanBytes;
        }

        /// <summary>Byte offset to start serving from, or -1 while still looking.</summary>
        public long Offset { get; private set; } = -1;

        public bool IsDone => Offset >= 0;

        /// <summary>"headers", "no-h264-or-hevc", "limit" or "end".</summary>
        public string Reason { get; private set; }

        /// <summary>Feeds the next bytes of the stream (from position 0, in order).</summary>
        public void Write(byte[] buffer, int offset, int count)
        {
            int end = offset + count;
            while (offset < end && !IsDone)
            {
                if (_packetCount == 0 && buffer[offset] != Sync)
                {
                    offset++;
                    _position++;
                    continue;
                }
                if (_packetCount == 0) _packetStart = _position;
                int take = Math.Min(PacketSize - _packetCount, end - offset);
                Buffer.BlockCopy(buffer, offset, _packet, _packetCount, take);
                _packetCount += take;
                offset += take;
                _position += take;
                if (_packetCount < PacketSize) break;

                _packetCount = 0;
                OnPacket(_packet, _packetStart);
                if (!IsDone && _position > _maxScanBytes) Finish(0, "limit");
            }
        }

        /// <summary>Call at end of input.</summary>
        public void Complete()
        {
            if (!IsDone) Finish(0, "end");
        }

        private void Finish(long offset, string reason)
        {
            Offset = offset;
            Reason = reason;
        }

        private void OnPacket(byte[] p, long at)
        {
            int pid = ((p[1] & 0x1F) << 8) | p[2];
            bool unitStart = (p[1] & 0x40) != 0;
            int adaptation = (p[3] >> 4) & 0x3;
            int payload = 4;
            if (adaptation == 2 || adaptation == 3) payload = 5 + p[4];
            bool hasPayload = (adaptation == 1 || adaptation == 3) && payload < PacketSize;

            if (pid == 0)
            {
                _lastPat = at;
                _pmtSinceLastPat = false;
                if (hasPayload && unitStart) ParsePat(p, payload);
                return;
            }
            if (pid == _pmtPid)
            {
                if (_lastPat >= 0) _pmtSinceLastPat = true;
                if (hasPayload && unitStart && _videoPid < 0)
                {
                    ParsePmt(p, payload);
                    if (_videoPid < 0) Finish(_lastPat >= 0 ? _lastPat : 0, "no-h264-or-hevc");
                }
                return;
            }
            if (pid != _videoPid || !hasPayload) return;

            if (unitStart)
            {
                // a new access unit: it can be the start only if a PAT and a PMT came before it
                _candidate = _lastPat >= 0 && _pmtSinceLastPat ? _lastPat : -1;
                _scanCount = 0;
                _scanDone = _candidate < 0;
                ScanPes(p, payload, true);
            }
            else
            {
                ScanPes(p, payload, false);
            }

            if (!_scanDone && ContainsSps()) Finish(_candidate, "headers");
        }

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
