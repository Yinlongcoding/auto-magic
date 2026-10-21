using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;

namespace AM.Bridge;

/// <summary>
/// 插件 ↔ NativeHost ↔ 桌面端的协议常量。插件端的同名常量在 plugin/protocol.js，两边要一起改。
/// </summary>
public static class BridgeProtocol
{
    public const string Version = "1";
    public const string PipeName = "AM.Bridge.v1";
    public const string HostName = "com.am.bridge";

    /// <summary>单帧上限。Chrome 规定 NativeHost 发给插件的单条消息不超过 1 MB。</summary>
    public const int MaxFrameBytes = 1024 * 1024;

    public static class Types
    {
        // 桌面端 → 插件
        public const string CollectStart = "collect.start";
        public const string CollectStop = "collect.stop";

        // 插件 → 桌面端
        public const string ExtensionHello = "extension.hello";
        public const string CollectAccepted = "collect.accepted";
        public const string CollectProgress = "collect.progress";
        public const string CollectItem = "collect.item";
        public const string CollectPassDone = "collect.passDone";
        public const string CollectFinished = "collect.finished";
        public const string CollectStopped = "collect.stopped";
        public const string CollectFailed = "collect.failed";

        // NativeHost → 插件
        public const string BridgeStatus = "bridge.status";
    }

    public static readonly IReadOnlySet<string> FromExtension = new HashSet<string>
    {
        Types.ExtensionHello, Types.CollectAccepted, Types.CollectProgress, Types.CollectItem,
        Types.CollectPassDone, Types.CollectFinished, Types.CollectStopped, Types.CollectFailed,
    };

    public static readonly IReadOnlySet<string> FromDesktop = new HashSet<string>
    {
        Types.CollectStart, Types.CollectStop,
    };
}

public static class BridgeJson
{
    public static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web)
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };
}

public sealed record BridgeError(string Code, string Message);

/// <summary>每条消息的信封。payload 按 type 解析。</summary>
public sealed record BridgeEnvelope
{
    public string ProtocolVersion { get; init; } = BridgeProtocol.Version;
    public required string RequestId { get; init; }
    public required string Type { get; init; }
    public JsonNode? Payload { get; init; }
    public BridgeError? Error { get; init; }

    public static BridgeEnvelope Create<T>(string type, T payload) => new()
    {
        RequestId = Guid.NewGuid().ToString("N"),
        Type = type,
        Payload = JsonSerializer.SerializeToNode(payload, BridgeJson.Options),
    };

    public static BridgeEnvelope Failure(string type, string code, string message) => new()
    {
        RequestId = Guid.NewGuid().ToString("N"),
        Type = type,
        Error = new BridgeError(code, message),
    };

    public T? PayloadAs<T>() => Payload is null ? default : Payload.Deserialize<T>(BridgeJson.Options);
}
