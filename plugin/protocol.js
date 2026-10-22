// 插件 ↔ NativeHost ↔ 桌面端的协议常量。C# 端的同名常量在 src/AM.Bridge/BridgeProtocol.cs，两边要一起改。

export const PROTOCOL_VERSION = "1";
export const HOST_NAME = "com.am.bridge";

export const Types = Object.freeze({
  // 插件 → 桌面端：探活。桌面端收到就回 bridge.pong。
  BridgePing: "bridge.ping",

  // 桌面端 → 插件
  CollectStart: "collect.start",
  CollectStop: "collect.stop",
  BridgePong: "bridge.pong",

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

  // 插件 → 桌面端：页面探测结果，仅开发阶段使用，接入真实采集后删除。
  ProbeResult: "probe.result",
});

export function envelope(type, payload, error) {
  // 类型写错时宁可本地报错，也不要发一条缺 type 的帧让对端解析失败。
  if (typeof type !== "string" || type.length === 0) {
    throw new Error(`消息缺少 type：${String(type)}`);
  }

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
