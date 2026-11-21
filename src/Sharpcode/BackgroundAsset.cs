using System.Security.Cryptography;
using Avalonia.Media.Imaging;
using Sharpcode.Core;
using SkiaSharp;

namespace Sharpcode;

internal sealed class BackgroundAsset(string name, string id, Bitmap bitmap, byte[]? pendingBytes = null) : IDisposable
{
    private const int MaxBytes = 24 * 1024 * 1024;
    private const int MaxDisplayEdge = 2560;
    private byte[]? pending = pendingBytes;

    public string Name { get; } = name;
    public string Id { get; } = id;
    public Bitmap Bitmap { get; } = bitmap;

    public static async Task<BackgroundAsset> ImportAsync(Stream source, string name, CancellationToken token)
    {
        var bytes = await ReadImageBytesAsync(source, token).ConfigureAwait(false);
        return await Task.Run(() =>
        {
            var hash = Convert.ToHexStringLower(SHA256.HashData(bytes));
            return new BackgroundAsset(name, hash, Decode(bytes), bytes);
        }, token).ConfigureAwait(false);
    }

    public static async Task<BackgroundAsset?> OpenSavedAsync(AppearanceSettings settings, CancellationToken token)
    {
        if (settings.BackgroundId is not { Length: 64 } id || !id.All(Uri.IsHexDigit))
            return null;
        var path = AssetPath(id);
        if (!File.Exists(path))
            return null;
        await using var source = File.OpenRead(path);
        var bytes = await ReadImageBytesAsync(source, token).ConfigureAwait(false);
        var bitmap = await Task.Run(() => Decode(bytes), token).ConfigureAwait(false);
        return new(settings.BackgroundName ?? "Background image", id, bitmap);
    }

    public async Task SaveAsync(CancellationToken token)
    {
        if (pending is not { } bytes)
            return;
        var path = AssetPath(Id);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var temporary = $"{path}.{Guid.NewGuid():N}.tmp";
        try
        {
            await File.WriteAllBytesAsync(temporary, bytes, token).ConfigureAwait(false);
            File.Move(temporary, path, overwrite: true);
            pending = null;
        }
        finally { File.Delete(temporary); }
    }

    public void Dispose() => Bitmap.Dispose();

    private static string AssetPath(string id) => Path.Combine(Preferences.DataDirectory, "backgrounds", $"{id}.image");

    private static async Task<byte[]> ReadImageBytesAsync(Stream source, CancellationToken token)
    {
        using var output = new MemoryStream();
        var buffer = new byte[64 * 1024];
        int length;
        while ((length = await source.ReadAsync(buffer, token).ConfigureAwait(false)) != 0)
        {
            if (output.Length + length > MaxBytes)
                throw new IOException("Choose an image smaller than 24 MB.");
            output.Write(buffer, 0, length);
        }
        return output.ToArray();
    }

    private static Bitmap Decode(byte[] bytes)
    {
        using var metadata = new MemoryStream(bytes, writable: false);
        using var codec = SKCodec.Create(metadata);
        if (codec is null || codec.EncodedFormat is not (SKEncodedImageFormat.Png or SKEncodedImageFormat.Jpeg or
            SKEncodedImageFormat.Webp or SKEncodedImageFormat.Bmp))
            throw new IOException("Choose a valid PNG, JPEG, WebP, or BMP image.");
        var size = codec.Info;
        if (size.Width <= 0 || size.Height <= 0 || Math.Max(size.Width, size.Height) > 16_384 ||
            (long)size.Width * size.Height > 48_000_000)
            throw new IOException("Choose an image up to 48 megapixels and 16,384 pixels on either side.");
        using var source = new MemoryStream(bytes, writable: false);
        if (Math.Max(size.Width, size.Height) <= MaxDisplayEdge)
            return new Bitmap(source);
        return size.Width >= size.Height
            ? Bitmap.DecodeToWidth(source, MaxDisplayEdge, BitmapInterpolationMode.HighQuality)
            : Bitmap.DecodeToHeight(source, MaxDisplayEdge, BitmapInterpolationMode.HighQuality);
    }
}
