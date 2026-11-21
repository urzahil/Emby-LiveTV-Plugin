using System;
using System.Collections.Generic;

namespace Emby.Xtream.Catchup
{
    /// <summary>
    /// Recently fetched windows of catch-up streams, kept in memory. To seek, ffmpeg makes a dozen or more small range
    /// requests, most of them within a few MB of each other; each one costs a provider round trip (0.1–0.3 s), so a seek took
    /// several seconds and Emby gave up or restarted it. With an 8 MB window per provider request, the nearby ones are
    /// answered from memory.
    /// </summary>
    public sealed class RangeWindowCache
    {
        public const int DefaultWindowSize = 8 * 1024 * 1024;

        private sealed class Window
        {
            public string Key;
            public long Start;
            public byte[] Data;
        }

        private readonly int _maxWindows;
        private readonly LinkedList<Window> _windows = new LinkedList<Window>(); // most recently used first

        public RangeWindowCache(int maxWindows = 6)
        {
            _maxWindows = Math.Max(1, maxWindows);
        }

        /// <summary>Finds a window of <paramref name="key"/> that contains <paramref name="position"/>.</summary>
        public bool TryGet(string key, long position, out byte[] data, out long start)
        {
            lock (_windows)
            {
                for (var node = _windows.First; node != null; node = node.Next)
                {
                    Window w = node.Value;
                    if (w.Key == key && position >= w.Start && position < w.Start + w.Data.Length)
                    {
                        _windows.Remove(node);
                        _windows.AddFirst(node);
                        data = w.Data;
                        start = w.Start;
                        return true;
                    }
                }
            }
            data = null;
            start = 0;
            return false;
        }

        public void Add(string key, long start, byte[] data)
        {
            if (data == null || data.Length == 0) return;
            lock (_windows)
            {
                _windows.AddFirst(new Window { Key = key, Start = start, Data = data });
                while (_windows.Count > _maxWindows) _windows.RemoveLast();
            }
        }
    }
}
