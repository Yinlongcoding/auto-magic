using System.Buffers.Binary;
using System.Text.Json;

namespace AutoMagic.Contracts.Protocol;

public static class LengthPrefixedJson
{
    public static async ValueTask<ProtocolEnvelope?> ReadEnvelopeAsync(
        Stream stream,
        CancellationToken cancellationToken)
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
        var envelope = JsonSerializer.Deserialize<ProtocolEnvelope>(payload, BridgeJson.Options)
            ?? throw new InvalidDataException("消息 JSON 为空。");
        ValidateEnvelope(envelope);
        return envelope;
    }

    public static async ValueTask WriteEnvelopeAsync(
        Stream stream,
        ProtocolEnvelope envelope,
        CancellationToken cancellationToken)
    {
        ValidateEnvelope(envelope);
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

    private static void ValidateEnvelope(ProtocolEnvelope envelope)
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

    private static async ValueTask<int> ReadExactlyOrEndAsync(
        Stream stream,
        Memory<byte> buffer,
        CancellationToken cancellationToken)
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
