using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Emby.Xtream.Xtream;

namespace Emby.Xtream.Tests
{
    internal static class Fixture
    {
        public static string Text(string name) => File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "TestData", name));
        public static Stream Stream(string name) => File.OpenRead(Path.Combine(AppContext.BaseDirectory, "TestData", name));
    }

    internal sealed class FakeHttp : IXtreamHttp
    {
        public readonly List<string> Requests = new List<string>();
        private readonly List<KeyValuePair<string, string>> _routes = new List<KeyValuePair<string, string>>();

        public FakeHttp Route(string urlContains, string body)
        {
            _routes.Add(new KeyValuePair<string, string>(urlContains, body));
            return this;
        }

        public Task<Stream> GetStreamAsync(string url, CancellationToken ct)
        {
            Requests.Add(url);
            foreach (var r in _routes)
                if (url.Contains(r.Key)) return Task.FromResult<Stream>(new MemoryStream(Encoding.UTF8.GetBytes(r.Value)));
            throw new InvalidOperationException("HTTP 404 for " + url);
        }
    }
}
