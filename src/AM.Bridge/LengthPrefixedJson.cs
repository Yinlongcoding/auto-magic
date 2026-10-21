using System.Buffers.Binary;
using System.Text.Json;

namespace AM.Bridge;

/// <summary>
/// 帧格式：4 字节小端长度 + UTF-8 JSON。Chrome native messaging 和命名管道都用这个格式。
/// 来自旧仓库 AutoMagic.Contracts，改了类型名和帧上限。
/// </summary>
public static class LengthPrefixedJson
{
    /// <summary>读一帧。流正常结束时返回 null。</summary>
    public static async ValueTask<BridgeEnvelope?> ReadAsync(Stream stream, CancellationToken cancellationToken)
    {
        var header = new byte[sizeof(int)];
        var headerBytes = await ReadExactlyOrEndAsync(stream, header, cancellationToken);
        if (headerBytes == 0)
        {
            return null;
        }

        if (headerBytes != header.Length)
        {
            throw new EndOfStreamException("消息长度头不完整。");
        }

        var length = BinaryPrimitives.ReadInt32LittleEndian(header);
        if (length is <= 0 or > BridgeProtocol.MaxFrameBytes)
        {
            throw new InvalidDataException($"消息长度无效：{length}。");
        }

        var payload = new byte[length];
        await stream.ReadExactlyAsync(payload, cancellationToken);
        var envelope = JsonSerializer.Deserialize<BridgeEnvelope>(payload, BridgeJson.Options)
            ?? throw new InvalidDataException("消息 JSON 为空。");
        Validate(envelope);
        return envelope;
    }

    public static async ValueTask WriteAsync(Stream stream, BridgeEnvelope envelope, CancellationToken cancellationToken)
    {
        Validate(envelope);
        var payload = JsonSerializer.SerializeToUtf8Bytes(envelope, BridgeJson.Options);
        if (payload.Length > BridgeProtocol.MaxFrameBytes)
        {
            throw new InvalidDataException($"消息超过 {BridgeProtocol.MaxFrameBytes} 字节限制。");
        }

        var header = new byte[sizeof(int)];
        BinaryPrimitives.WriteInt32LittleEndian(header, payload.Length);
        await stream.WriteAsync(header, cancellationToken);
        await stream.WriteAsync(payload, cancellationToken);
        await stream.FlushAsync(cancellationToken);
    }

    private static void Validate(BridgeEnvelope envelope)
    {
        if (envelope.ProtocolVersion != BridgeProtocol.Version)
        {
            throw new InvalidDataException(
                $"不支持的协议版本：{envelope.ProtocolVersion}，当前为 {BridgeProtocol.Version}。");
        }

        if (string.IsNullOrWhiteSpace(envelope.RequestId) || string.IsNullOrWhiteSpace(envelope.Type))
        {
            throw new InvalidDataException("消息缺少 requestId 或 type。");
        }
    }

    private static async ValueTask<int> ReadExactlyOrEndAsync(Stream stream, Memory<byte> buffer, CancellationToken cancellationToken)
    {
        var total = 0;
        while (total < buffer.Length)
        {
            var read = await stream.ReadAsync(buffer[total..], cancellationToken);
            if (read == 0)
            {
                return total;
            }

            total += read;
        }

        return total;
    }
}
