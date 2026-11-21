using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;

namespace Emby.Xtream.Catchup
{
    /// <summary>Read-only stream: some already-read bytes, then the rest of another stream.</summary>
    public class PrefixedStream : Stream
    {
        private readonly byte[] _prefix;
        private int _prefixPosition;
        private readonly int _prefixEnd;
        private readonly Stream _inner;
        private readonly IDisposable _owner;

        /// <param name="owner">Disposed together with the stream, e.g. the HTTP response <paramref name="inner"/> belongs to.</param>
        public PrefixedStream(byte[] prefix, int offset, int count, Stream inner, IDisposable owner = null)
        {
            _prefix = prefix;
            _prefixPosition = offset;
            _prefixEnd = offset + count;
            _inner = inner;
            _owner = owner;
        }

        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }

        public override int Read(byte[] buffer, int offset, int count)
        {
            int fromPrefix = TakePrefix(buffer, offset, count);
            return fromPrefix > 0 ? fromPrefix : _inner.Read(buffer, offset, count);
        }

        public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken)
        {
            int fromPrefix = TakePrefix(buffer, offset, count);
            return fromPrefix > 0 ? Task.FromResult(fromPrefix) : _inner.ReadAsync(buffer, offset, count, cancellationToken);
        }

        private int TakePrefix(byte[] buffer, int offset, int count)
        {
            int n = Math.Min(count, _prefixEnd - _prefixPosition);
            if (n <= 0) return 0;
            Buffer.BlockCopy(_prefix, _prefixPosition, buffer, offset, n);
            _prefixPosition += n;
            return n;
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
                try { _owner?.Dispose(); } catch (Exception) { /* already closed */ }
            }
            base.Dispose(disposing);
        }
    }
}
