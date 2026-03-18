using SkiaSharp;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace InfoPanel.Utils
{
    public static class FontCache
    {
        private static List<string>? _fonts;
        private static Task<List<string>>? _loadTask;
        private static readonly object _lock = new();

        public static Task<List<string>> GetFontsAsync()
        {
            if (_fonts != null)
                return Task.FromResult(_fonts);

            lock (_lock)
            {
                _loadTask ??= Task.Run(() =>
                {
                    var result = SKFontManager.Default.GetFontFamilies()
                        .OrderBy(f => f)
                        .ToList();
                    Interlocked.Exchange(ref _fonts, result);
                    return result;
                });
                return _loadTask;
            }
        }
    }
}
