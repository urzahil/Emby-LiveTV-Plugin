using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Net;
using System.Threading;
using System.Threading.Tasks;
using Emby.Xtream.Catchup;
using Emby.Xtream.Xtream;
using MediaBrowser.Common.Extensions;
using MediaBrowser.Common.Net;
using MediaBrowser.Controller.Net;
using MediaBrowser.Model.Logging;
using MediaBrowser.Model.Net;
using MediaBrowser.Model.Services;

namespace Emby.Xtream.Api
{
    // Emby makes every route require a signed-in user unless it is marked [Unauthenticated]. Emby's own ffmpeg sends no
    // token, so without this it gets 401. CatchupStreamService checks loopback + token instead.
    [Route("/Xtream/Catchup/{Token}", "GET", IsHidden = true, Summary = "Catch-up stream for Emby's own ffmpeg (loopback only)")]
    [Unauthenticated]
    public class GetCatchupStream
    {
        public string Token { get; set; }
    }

    /// <summary>
    /// Serves a catch-up programme to Emby's own ffmpeg/ffprobe. The served stream is the provider's timeshift stream from
    /// the offset <see cref="TsStartFinder"/> picks (so it starts with the video headers), byte for byte. When the provider
    /// supports byte ranges this endpoint does too, shifted by that offset, so ffmpeg's <c>-ss</c> seeks instead of reading
    /// through the whole programme.
    /// <para>Not authenticated, because ffmpeg has no Emby token; instead it answers only requests from the server itself,
    /// and only for a random token that <see cref="CatchupStreamRegistry"/> issued.</para>
    /// </summary>
    public class CatchupStreamService : IService, IRequiresRequest
    {
        private static readonly TimeSpan ConnectTimeout = TimeSpan.FromSeconds(30);
        // A provider that allows one stream at a time answers 503 until the previous connection has closed on its side
        // (the plugin closes its own previous connection for the same programme first, see CatchupStreamRegistry.ReplaceActive).
        private static readonly TimeSpan BusyRetryDelay = TimeSpan.FromMilliseconds(500);
        private const int BusyRetries = 16;
        private const int MaxBufferedStart = 32 * 1024 * 1024;

        private readonly IHttpClient _http;
        private readonly ILogger _logger;

        public CatchupStreamService(IHttpClient httpClient, ILogManager logManager)
        {
            _http = httpClient;
            _logger = logManager.GetLogger("XtreamCatchup");
        }

        public IRequest Request { get; set; }

        public async Task<object> Get(GetCatchupStream request)
        {
            IPAddress remote = Request?.RemoteIp;
            if (remote != null && remote.IsIPv4MappedToIPv6) remote = remote.MapToIPv4();
            CatchupStreamRegistry.Entry entry;
            // the same answer for both cases, so the endpoint does not reveal which tokens exist
            if (remote == null || !IPAddress.IsLoopback(remote) || !CatchupStreamRegistry.TryResolve(request.Token, DateTime.UtcNow, out entry))
                throw new ResourceNotFoundException();

            CancellationToken ct = Request.CancellationToken;
            ByteRange range = ByteRange.Parse(Request.Headers?["Range"]);
            if (range != null && range.From == 0) range = range.To.HasValue ? range : null; // "bytes=0-" is a plain read

            if (range != null && entry.StartOffset.HasValue && entry.SeekableLength.HasValue)
            {
                object ranged = await ServeRange(request.Token, entry, range, ct).ConfigureAwait(false);
                if (ranged != null) return ranged;
            }

            return await ServeFromStart(request.Token, entry, range, ct).ConfigureAwait(false);
        }

        /// <summary>Opens the provider at offset 0, finds the start offset and serves from it; then serves a pending range.</summary>
        private async Task<object> ServeFromStart(string token, CatchupStreamRegistry.Entry entry, ByteRange range, CancellationToken ct)
        {
            HttpResponseInfo response = await Open(token, entry, null, ct).ConfigureAwait(false);
            byte[] buffered;
            int count;
            long start;
            try
            {
                var finder = new TsStartFinder();
                buffered = new byte[256 * 1024];
                count = 0;
                while (!finder.IsDone)
                {
                    if (count == buffered.Length)
                    {
                        if (buffered.Length >= MaxBufferedStart) { finder.Complete(); break; }
                        Array.Resize(ref buffered, Math.Min(buffered.Length * 2, MaxBufferedStart));
                    }
                    int n = await response.Content.ReadAsync(buffered, count, buffered.Length - count, ct).ConfigureAwait(false);
                    if (n == 0) { finder.Complete(); break; }
                    finder.Write(buffered, count, n);
                    count += n;
                }
                start = finder.Offset;
                if (start > count) start = 0;

                // a known length is taken as seekable; the first byte range the provider does not honour turns that off (ServeRange)
                long? providerLength = response.StatusCode == HttpStatusCode.OK && !RefusesRanges(response) ? response.ContentLength : null;
                long? servedLength = providerLength.HasValue && providerLength.Value > start ? providerLength.Value - start : (long?)null;
                CatchupStreamRegistry.SetLayout(token, start, servedLength);
                if (!entry.StartOffset.HasValue)
                {
                    _logger.Info("Catch-up stream starts after skipping {0} KB ({1}){2}", start / 1024, finder.Reason,
                        servedLength.HasValue ? ", seekable" : ", not seekable");
                }
                entry.StartOffset = start;
                entry.SeekableLength = servedLength;
            }
            catch
            {
                response.Dispose();
                throw;
            }

            if (range != null && entry.SeekableLength.HasValue)
            {
                // the first request was already a seek: the layout is known now, reopen at the right place
                response.Dispose();
                object ranged = await ServeRange(token, entry, range, ct).ConfigureAwait(false);
                if (ranged != null) return ranged;
                response = await Open(token, entry, null, ct).ConfigureAwait(false);
                return Result(new PrefixedStream(new byte[0], 0, 0, response.Content), 200, null, null);
            }

            var body = new PrefixedStream(buffered, (int)start, count - (int)start, response.Content);
            if (!entry.SeekableLength.HasValue) return Result(body, 200, null, null);
            long length = entry.SeekableLength.Value;
            return range == null
                ? Result(body, 200, length, null)
                : Result(body, 206, length, "bytes 0-" + (length - 1).ToString(CultureInfo.InvariantCulture) + "/" + length.ToString(CultureInfo.InvariantCulture));
        }

        /// <summary>Serves a byte range of the served stream from the same range of the provider file; null if the provider did not honour it.</summary>
        private async Task<object> ServeRange(string token, CatchupStreamRegistry.Entry entry, ByteRange range, CancellationToken ct)
        {
            long length = entry.SeekableLength.Value;
            if (range.From >= length) return Result(new MemoryStream(new byte[0]), 416, null, "bytes */" + length.ToString(CultureInfo.InvariantCulture));

            long last = range.LastByte(length);
            // clamp before shifting, so a huge end value from the caller cannot overflow
            HttpResponseInfo response = await Open(token, entry, new ByteRange(range.From, last).ToHeader(entry.StartOffset.Value), ct).ConfigureAwait(false);
            if (response.StatusCode != HttpStatusCode.PartialContent)
            {
                response.Dispose();
                CatchupStreamRegistry.SetLayout(token, entry.StartOffset.Value, null);
                entry.SeekableLength = null;
                _logger.Warn("Catch-up provider ignored a byte range; seeking is not available for this programme");
                return null;
            }
            string contentRange = "bytes " + range.From.ToString(CultureInfo.InvariantCulture) + "-" + last.ToString(CultureInfo.InvariantCulture) +
                                  "/" + length.ToString(CultureInfo.InvariantCulture);
            return Result(new PrefixedStream(new byte[0], 0, 0, response.Content), 206, last - range.From + 1, contentRange);
        }

        private async Task<HttpResponseInfo> Open(string token, CatchupStreamRegistry.Entry entry, string rangeHeader, CancellationToken ct)
        {
            // the connection a superseded request for this programme still holds would make the provider answer 503
            CatchupStreamRegistry.ReplaceActive(token, null);
            for (int attempt = 0; ; attempt++)
            {
                var options = new HttpRequestOptions
                {
                    Url = entry.Url,
                    CancellationToken = ct,
                    UserAgent = string.IsNullOrWhiteSpace(entry.UserAgent) ? XtreamDefaults.UserAgent : entry.UserAgent,
                    TimeoutMs = (int)ConnectTimeout.TotalMilliseconds,
                    EnableHttpCompression = false,
                    BufferContent = false,
                    // the URL holds the provider password
                    LogRequest = false,
                    LogErrors = false,
                    ThrowOnErrorResponse = true
                };
                if (rangeHeader != null) options.RequestHeaders["Range"] = rangeHeader;

                try
                {
                    HttpResponseInfo response = await _http.GetResponse(options).ConfigureAwait(false);
                    CatchupStreamRegistry.ReplaceActive(token, response);
                    return response;
                }
                catch (OperationCanceledException) { throw; }
                catch (HttpException ex) when (ex.StatusCode == HttpStatusCode.ServiceUnavailable && attempt < BusyRetries)
                {
                    await Task.Delay(BusyRetryDelay, ct).ConfigureAwait(false);
                }
                catch (Exception ex)
                {
                    string message = XtreamUrlBuilder.SanitizeText(ex.Message);
                    _logger.Error("Catch-up stream could not be opened: {0}", message);
                    throw new InvalidOperationException("Catch-up stream could not be opened: " + message);
                }
            }
        }

        private static bool RefusesRanges(HttpResponseInfo response)
        {
            string value;
            return response.Headers != null && response.Headers.TryGetValue("Accept-Ranges", out value) &&
                   string.Equals(value?.Trim(), "none", StringComparison.OrdinalIgnoreCase);
        }

        private static TsResponse Result(Stream body, int status, long? contentLength, string contentRange)
        {
            var result = new TsResponse(body) { Status = status };
            if (contentLength.HasValue || contentRange != null) result.Headers["Accept-Ranges"] = "bytes";
            if (contentLength.HasValue) result.Headers["Content-Length"] = contentLength.Value.ToString(CultureInfo.InvariantCulture);
            if (contentRange != null) result.Headers["Content-Range"] = contentRange;
            return result;
        }

        /// <summary>The response body plus the status and headers Emby writes before copying it.</summary>
        private sealed class TsResponse : PrefixedStream, IHttpResult
        {
            public TsResponse(Stream body) : base(new byte[0], 0, 0, body) { }

            public IDictionary<string, string> Headers { get; } = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            public int Status { get; set; } = 200;
            public HttpStatusCode StatusCode { get => (HttpStatusCode)Status; set => Status = (int)value; }
            public string ContentType { get; set; } = "video/mp2t";
            public IRequest RequestContext { get; set; }
        }
    }
}
