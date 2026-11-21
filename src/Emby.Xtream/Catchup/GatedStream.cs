using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;

namespace Emby.Xtream.Catchup
{
    /// <summary>Read-only stream that passes a provider stream through a <see cref="TsHeaderGate"/>.</summary>
    public class GatedStream : Stream
    {
        private readonly Stream _inner;
        private readonly TsHeaderGate _gate;
        private readonly Action<TsHeaderGate> _onOpen;
        private readonly byte[] _readBuffer = new byte[64 * 1024];
        private readonly MemoryStream _pending = new MemoryStream();
        private long _pendingRead;
        private bool _innerEnded;
        private bool _reportedOpen;

        public GatedStream(Stream inner, TsHeaderGate gate, Action<TsHeaderGate> onOpen = null)
        {
            _inner = inner;
            _gate = gate;
            _onOpen = onOpen;
        }

        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }

        public override int Read(byte[] buffer, int offset, int count) =>
            ReadAsync(buffer, offset, count, CancellationToken.None).GetAwaiter().GetResult();

        public override async Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken)
        {
            if (count == 0) return 0;
            while (true)
            {
                int available = (int)(_pending.Length - _pendingRead);
                if (available > 0)
                {
                    int n = Math.Min(available, count);
                    Buffer.BlockCopy(_pending.GetBuffer(), (int)_pendingRead, buffer, offset, n);
                    _pendingRead += n;
                    if (_pendingRead == _pending.Length) { _pending.SetLength(0); _pendingRead = 0; }
                    return n;
                }

                if (_innerEnded) return 0;

                if (_gate.IsOpen)
                {
                    // fast path: nothing buffered, read straight into the caller's buffer
                    int direct = await _inner.ReadAsync(buffer, offset, count, cancellationToken).ConfigureAwait(false);
                    if (direct == 0) _innerEnded = true;
                    return direct;
                }

                int read = await _inner.ReadAsync(_readBuffer, 0, _readBuffer.Length, cancellationToken).ConfigureAwait(false);
                if (read == 0)
                {
                    _innerEnded = true;
                    _gate.Complete(_pending);
                }
                else
                {
                    _gate.Write(_readBuffer, 0, read, _pending);
                }
                if (_gate.IsOpen && !_reportedOpen)
                {
                    _reportedOpen = true;
                    _onOpen?.Invoke(_gate);
                }
            }
        }

        public override void Flush() { }
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                _inner.Dispose();
                _pending.Dispose();
            }
            base.Dispose(disposing);
        }
    }
}
