using System.Buffers.Binary;
using System.Text.Json;

namespace SaruPdf.Instances;

internal static class PipeProtocol
{
    public const int MaximumBytes = 1024 * 1024;

    public static async Task WriteAsync<T>(Stream stream, T message, CancellationToken token)
    {
        var bytes = JsonSerializer.SerializeToUtf8Bytes(message);
        if (bytes.Length > MaximumBytes) throw new InvalidDataException("一度に開くファイルが多すぎます。");
        byte[] prefix = new byte[4];
        BinaryPrimitives.WriteInt32LittleEndian(prefix, bytes.Length);
        await stream.WriteAsync(prefix, token);
        await stream.WriteAsync(bytes, token);
        await stream.FlushAsync(token);
    }

    public static async Task<T> ReadAsync<T>(Stream stream, CancellationToken token)
    {
        byte[] prefix = new byte[4];
        await stream.ReadExactlyAsync(prefix, token);
        int length = BinaryPrimitives.ReadInt32LittleEndian(prefix);
        if (length <= 0 || length > MaximumBytes) throw new InvalidDataException("引き継ぎデータのサイズが不正です。");
        byte[] bytes = new byte[length];
        await stream.ReadExactlyAsync(bytes, token);
        return JsonSerializer.Deserialize<T>(bytes) ?? throw new InvalidDataException("引き継ぎデータが空です。");
    }
}
