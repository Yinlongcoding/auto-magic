// 插件 ↔ NativeHost ↔ 桌面端的协议常量。C# 端的同名常量在 src/AM.Bridge/BridgeProtocol.cs，两边要一起改。

export const PROTOCOL_VERSION = "1";
export const HOST_NAME = "com.am.bridge";

export const Types = Object.freeze({
  // 桌面端 → 插件
  CollectStart: "collect.start",
  CollectStop: "collect.stop",

  // 插件 → 桌面端
  ExtensionHello: "extension.hello",
  CollectAccepted: "collect.accepted",
  CollectProgress: "collect.progress",
  CollectItem: "collect.item",
  CollectPassDone: "collect.passDone",
  CollectFinished: "collect.finished",
  CollectStopped: "collect.stopped",
  CollectFailed: "collect.failed",

  // NativeHost → 插件
  BridgeStatus: "bridge.status",
});

export function envelope(type, payload, error) {
  const message = {
    protocolVersion: PROTOCOL_VERSION,
    requestId: crypto.randomUUID().replaceAll("-", ""),
    type,
    payload,
  };
  if (error) {
    message.error = error;
  }
  return message;
}
