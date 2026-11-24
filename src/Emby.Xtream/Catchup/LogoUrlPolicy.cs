using System;
using System.Net;
using System.Net.Sockets;
using System.Threading;
using System.Threading.Tasks;

namespace Emby.Xtream.Catchup
{
    /// <summary>
    /// Decides which logo URLs the plugin may fetch. A panel supplies <c>stream_icon</c>, so it is untrusted input: only
    /// http(s) URLs are fetched, and not ones that point at the server itself or at link-local addresses (cloud metadata
    /// services, local admin ports). Private LAN addresses stay allowed, since a provider behind a local proxy is normal.
    /// <para>Names are resolved here, but the HTTP client resolves again, so this narrows the exposure rather than closing it
    /// (DNS rebinding and redirects are not covered).</para>
    /// </summary>
    public static class LogoUrlPolicy
    {
        /// <summary>True for an absolute http/https URL without credentials in it.</summary>
        public static bool TryParse(string url, out Uri uri)
        {
            uri = null;
            Uri parsed;
            if (!Uri.TryCreate(url, UriKind.Absolute, out parsed)) return false;
            if (parsed.Scheme != Uri.UriSchemeHttp && parsed.Scheme != Uri.UriSchemeHttps) return false;
            if (string.IsNullOrEmpty(parsed.Host) || !string.IsNullOrEmpty(parsed.UserInfo)) return false;
            uri = parsed;
            return true;
        }

        public static bool IsBlockedAddress(IPAddress address)
        {
            if (address == null) return true;
            if (address.IsIPv4MappedToIPv6) address = address.MapToIPv4();
            if (IPAddress.IsLoopback(address)) return true;
            if (address.AddressFamily == AddressFamily.InterNetwork)
            {
                byte[] b = address.GetAddressBytes();
                return b[0] == 0 || (b[0] == 169 && b[1] == 254);
            }
            if (address.AddressFamily == AddressFamily.InterNetworkV6)
            {
                return address.Equals(IPAddress.IPv6Any) || address.IsIPv6LinkLocal || address.IsIPv6SiteLocal;
            }
            return true;
        }

        public static bool IsBlockedHostName(string host)
        {
            host = host.TrimEnd('.');
            return host.Equals("localhost", StringComparison.OrdinalIgnoreCase) || host.EndsWith(".localhost", StringComparison.OrdinalIgnoreCase);
        }

        /// <summary>True when the URL is http(s) and its host is neither blocked by name nor resolves to a blocked address.</summary>
        public static async Task<bool> IsAllowedAsync(string url, CancellationToken ct)
        {
            Uri uri;
            if (!TryParse(url, out uri)) return false;
            if (IsBlockedHostName(uri.Host)) return false;

            IPAddress literal;
            if (IPAddress.TryParse(uri.Host.Trim('[', ']'), out literal)) return !IsBlockedAddress(literal);

            IPAddress[] addresses;
            try
            {
                addresses = await Dns.GetHostAddressesAsync(uri.DnsSafeHost).ConfigureAwait(false);
            }
            catch (Exception)
            {
                // cannot be resolved: the download will fail by itself
                return true;
            }
            ct.ThrowIfCancellationRequested();
            foreach (var a in addresses) if (IsBlockedAddress(a)) return false;
            return true;
        }
    }
}
