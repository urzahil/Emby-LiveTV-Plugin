using System.IO;
using System.Threading;
using System.Threading.Tasks;

namespace Emby.Xtream.Xtream
{
    /// <summary>Minimal HTTP abstraction so the Xtream layer is testable without Emby.</summary>
    public interface IXtreamHttp
    {
        /// <summary>GET the URL and return the response body stream. The caller disposes it. Throws on non-success status.</summary>
        Task<Stream> GetStreamAsync(string url, CancellationToken cancellationToken);
    }
}
