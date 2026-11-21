using System.Security.Cryptography;
using System.Text;

namespace Sharpcode.Core;

public sealed record FileSnapshot(string Text, Encoding Encoding, byte[] Version)
{
    public static FileSnapshot New { get; } = new("", new UTF8Encoding(false, true), []);
}

public static class TextFiles
{
    public const int EditorLimit = 64 * 1024 * 1024;
    public const int AgentLimit = 1024 * 1024;

    public static async Task<FileSnapshot> ReadAsync(string path, int limit, CancellationToken cancellationToken)
    {
        await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read,
            FileShare.ReadWrite | FileShare.Delete, 64 * 1024, FileOptions.Asynchronous | FileOptions.SequentialScan);
        if (stream.Length > limit)
            throw new IOException($"File exceeds the {limit / 1024:N0} KiB limit.");

        var bytes = GC.AllocateUninitializedArray<byte>((int)stream.Length);
        await stream.ReadExactlyAsync(bytes, cancellationToken).ConfigureAwait(false);
        if (stream.ReadByte() != -1)
            throw new IOException("The file grew while it was being read.");

        var encoding = DetectEncoding(bytes);
        var text = encoding.GetString(bytes, encoding.GetPreamble().Length, bytes.Length - encoding.GetPreamble().Length);
        if (text.Contains('\0'))
            throw new IOException("This file contains binary data.");

        return new(text, encoding, SHA256.HashData(bytes));
    }

    public static async Task<byte[]> WriteAsync(string path, string text, Encoding encoding, byte[] expectedVersion,
        CancellationToken cancellationToken)
    {
        var folder = Path.GetDirectoryName(path)!;
        var temporary = Path.Combine(folder, $".sharpcode-{Guid.NewGuid():N}.tmp");
        try
        {
            byte[] version;
            await using (var stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None,
                64 * 1024, FileOptions.Asynchronous))
            using (var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256))
            {
                var preamble = encoding.GetPreamble();
                var buffer = new byte[encoding.GetMaxByteCount(16 * 1024)];
                await stream.WriteAsync(preamble, cancellationToken).ConfigureAwait(false);
                hash.AppendData(preamble);
                var encoder = encoding.GetEncoder();
                for (var offset = 0; offset < text.Length;)
                {
                    var length = Math.Min(16 * 1024, text.Length - offset);
                    var count = encoder.GetBytes(text.AsSpan(offset, length), buffer, offset + length == text.Length);
                    await stream.WriteAsync(buffer.AsMemory(0, count), cancellationToken).ConfigureAwait(false);
                    hash.AppendData(buffer.AsSpan(0, count));
                    offset += length;
                }
                version = hash.GetHashAndReset();
            }

            if (expectedVersion.Length > 0)
            {
                await using var original = new FileStream(path, FileMode.Open, FileAccess.Read,
                    FileShare.ReadWrite | FileShare.Delete, 64 * 1024, FileOptions.Asynchronous | FileOptions.SequentialScan);
                var actual = await SHA256.HashDataAsync(original, cancellationToken).ConfigureAwait(false);
                if (!actual.AsSpan().SequenceEqual(expectedVersion))
                    throw new IOException("The file changed on disk. Reopen it before saving.");

                if (!OperatingSystem.IsWindows())
                    File.SetUnixFileMode(temporary, File.GetUnixFileMode(path));
            }

            cancellationToken.ThrowIfCancellationRequested();
            File.Move(temporary, path, overwrite: expectedVersion.Length > 0);
            return version;
        }
        finally
        {
            File.Delete(temporary);
        }
    }

    private static Encoding DetectEncoding(ReadOnlySpan<byte> bytes)
    {
        if (bytes.StartsWith(new byte[] { 0xff, 0xfe, 0, 0 }))
            return new UTF32Encoding(false, true, true);
        if (bytes.StartsWith(new byte[] { 0, 0, 0xfe, 0xff }))
            return new UTF32Encoding(true, true, true);
        if (bytes.StartsWith(new byte[] { 0xff, 0xfe }))
            return new UnicodeEncoding(false, true, true);
        if (bytes.StartsWith(new byte[] { 0xfe, 0xff }))
            return new UnicodeEncoding(true, true, true);
        return new UTF8Encoding(bytes.StartsWith(new byte[] { 0xef, 0xbb, 0xbf }), true);
    }
}
