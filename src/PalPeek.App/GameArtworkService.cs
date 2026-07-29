using System.Collections.Concurrent;
using System.Net.Http;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Color = System.Windows.Media.Color;
using Pen = System.Windows.Media.Pen;

namespace PalPeek;

public sealed class GameArtworkService : IDisposable
{
    private const int MaxImageBytes = 5 * 1024 * 1024;
    private static readonly TimeSpan CacheLifetime = TimeSpan.FromDays(30);
    private readonly HttpClient _http = new()
    {
        Timeout = TimeSpan.FromSeconds(6)
    };
    private readonly string _cacheDirectory = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "PalPeek",
        "Cache",
        "Artwork");
    private readonly ConcurrentDictionary<uint, Task<ImageSource>> _memory = new();

    public Task<ImageSource> GetArtworkAsync(uint appId, string gameName)
    {
        if (appId == 0)
            return Task.FromResult(CreatePlaceholder(appId, gameName));
        return _memory.GetOrAdd(appId, _ => LoadArtworkAsync(appId, gameName));
    }

    public ImageSource CreatePlaceholder(uint appId, string gameName)
    {
        var seed = HashCode.Combine(appId, gameName);
        var hue = Math.Abs(seed % 4);
        var accent = hue switch
        {
            0 => Color.FromRgb(53, 230, 196),
            1 => Color.FromRgb(124, 92, 255),
            2 => Color.FromRgb(49, 139, 192),
            _ => Color.FromRgb(255, 95, 112)
        };
        var group = new DrawingGroup();
        using (var drawing = group.Open())
        {
            drawing.DrawRectangle(
                new LinearGradientBrush(
                    Color.FromRgb(10, 18, 31),
                    Color.FromRgb(22, 34, 52),
                    25),
                null,
                new System.Windows.Rect(0, 0, 640, 300));
            drawing.DrawEllipse(
                new SolidColorBrush(Color.FromArgb(42, accent.R, accent.G, accent.B)),
                null,
                new System.Windows.Point(500, 80),
                180,
                180);
            drawing.DrawGeometry(
                new SolidColorBrush(Color.FromRgb(8, 15, 27)),
                null,
                Geometry.Parse("M0,300 L0,245 L150,94 L250,202 L342,65 L520,260 L640,145 L640,300 Z"));
            var pen = new Pen(
                new SolidColorBrush(Color.FromArgb(115, accent.R, accent.G, accent.B)),
                2);
            drawing.DrawLine(pen, new System.Windows.Point(36, 42),
                new System.Windows.Point(250, 42));
            drawing.DrawLine(pen, new System.Windows.Point(36, 42),
                new System.Windows.Point(36, 104));
            drawing.DrawEllipse(null, pen, new System.Windows.Point(505, 150), 48, 48);
            drawing.DrawLine(pen, new System.Windows.Point(457, 150),
                new System.Windows.Point(553, 150));
            drawing.DrawLine(pen, new System.Windows.Point(505, 102),
                new System.Windows.Point(505, 198));
        }
        group.Freeze();
        return new DrawingImage(group);
    }

    private async Task<ImageSource> LoadArtworkAsync(uint appId, string gameName)
    {
        var placeholder = CreatePlaceholder(appId, gameName);
        var cachePath = Path.Combine(_cacheDirectory, $"{appId}.jpg");
        try
        {
            if (File.Exists(cachePath) &&
                DateTime.UtcNow - File.GetLastWriteTimeUtc(cachePath) <= CacheLifetime)
            {
                return await LoadFileAsync(cachePath);
            }

            var image = await DownloadAsync(appId);
            if (image is not null)
            {
                try
                {
                    Directory.CreateDirectory(_cacheDirectory);
                    var temporaryPath = $"{cachePath}.{Guid.NewGuid():N}.tmp";
                    await File.WriteAllBytesAsync(temporaryPath, image);
                    File.Move(temporaryPath, cachePath, true);
                }
                catch
                {
                    // Artwork remains usable in memory when Windows blocks the cache.
                }
                return LoadBitmap(image);
            }

            if (File.Exists(cachePath))
                return await LoadFileAsync(cachePath);
        }
        catch
        {
            try
            {
                if (File.Exists(cachePath))
                    return await LoadFileAsync(cachePath);
            }
            catch
            {
            }
        }
        return placeholder;
    }

    private async Task<byte[]?> DownloadAsync(uint appId)
    {
        var uri =
            $"https://shared.fastly.steamstatic.com/store_item_assets/steam/apps/{appId}/header.jpg";
        using var response = await _http.GetAsync(
            uri,
            HttpCompletionOption.ResponseHeadersRead);
        if (!response.IsSuccessStatusCode ||
            response.Content.Headers.ContentType?.MediaType?.StartsWith(
                "image/",
                StringComparison.OrdinalIgnoreCase) != true ||
            response.Content.Headers.ContentLength > MaxImageBytes)
        {
            return null;
        }

        await using var source = await response.Content.ReadAsStreamAsync();
        using var destination = new MemoryStream();
        var buffer = new byte[81920];
        while (true)
        {
            var read = await source.ReadAsync(buffer);
            if (read == 0)
                break;
            if (destination.Length + read > MaxImageBytes)
                return null;
            await destination.WriteAsync(buffer.AsMemory(0, read));
        }
        return destination.ToArray();
    }

    private static async Task<ImageSource> LoadFileAsync(string path) =>
        LoadBitmap(await File.ReadAllBytesAsync(path));

    private static ImageSource LoadBitmap(byte[] bytes)
    {
        using var stream = new MemoryStream(bytes, writable: false);
        var image = new BitmapImage();
        image.BeginInit();
        image.CacheOption = BitmapCacheOption.OnLoad;
        image.DecodePixelWidth = 640;
        image.StreamSource = stream;
        image.EndInit();
        image.Freeze();
        return image;
    }

    public void Dispose() => _http.Dispose();
}
