using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;

namespace Emby.Xtream.Catchup
{
    /// <summary>
    /// Read-only stream for a byte range: first the part that is already in memory, then, only if the reader wants more,
    /// a stream opened at the first byte after it (by <c>openRest</c>, given that position). Readers that stop early, like
    /// ffmpeg's seek probes, never cause a second provider request.
    /// </summary>
    public sealed class WindowThenStream : Stream
    {
        private readonly byte[] _window;
        private int _position;
        private readonly int _end;
        private readonly long _restFrom;
        private readonly bool _hasRest;
        private readonly Func<long, CancellationToken, Task<Stream>> _openRest;
        private Stream _rest;

        /// <param name="window">Bytes in memory.</param>
        /// <param name="offset">First byte of <paramref name="window"/> to serve.</param>
        /// <param name="count">How many bytes of <paramref name="window"/> to serve.</param>
        /// <param name="restFrom">Position (in the caller's numbering) of the byte after those; null when nothing follows.</param>
        public WindowThenStream(byte[] window, int offset, int count, long? restFrom, Func<long, CancellationToken, Task<Stream>> openRest)
        {
            _window = window;
            _position = offset;
            _end = offset + count;
            _hasRest = restFrom.HasValue && openRest != null;
            _restFrom = restFrom ?? 0;
            _openRest = openRest;
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
            int inWindow = _end - _position;
            if (inWindow > 0)
            {
                int n = Math.Min(count, inWindow);
                Buffer.BlockCopy(_window, _position, buffer, offset, n);
                _position += n;
                return n;
            }
            if (!_hasRest) return 0;
            if (_rest == null) _rest = await _openRest(_restFrom, cancellationToken).ConfigureAwait(false);
            return await _rest.ReadAsync(buffer, offset, count, cancellationToken).ConfigureAwait(false);
        }

        public override void Flush() { }
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

        protected override void Dispose(bool disposing)
        {
            if (disposing) _rest?.Dispose();
            base.Dispose(disposing);
        }
    }
}
