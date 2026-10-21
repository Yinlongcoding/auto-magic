// 后台：连接 NativeHost，接收桌面端指令，同一时间只跑一个采集。
// 桌面端未启动时 NativeHost 回 bridge.status 错误并退出，这里定时重连。

import { HOST_NAME, Types, envelope } from "./protocol.js";
import { run } from "./fake-collector.js";

const RECONNECT_ALARM = "reconnect";

let port = null;
let current = null; // { batchId, controller }

function connect() {
  if (port) {
    return;
  }

  port = chrome.runtime.connectNative(HOST_NAME);
  port.onMessage.addListener(onMessage);
  port.onDisconnect.addListener(() => {
    // 读一下 lastError，避免控制台报"未检查的错误"。
    const reason = chrome.runtime.lastError?.message;
    console.info("AM: 与桌面端断开", reason ?? "");
    port = null;
    current?.controller.abort();
    current = null;
    setBadge(false);
  });

  send(Types.ExtensionHello, { extensionVersion: chrome.runtime.getManifest().version, collector: "fake" });
  setBadge(true);
}

function send(type, payload, error) {
  port?.postMessage(envelope(type, payload, error));
}

function onMessage(message) {
  switch (message.type) {
    case Types.BridgeStatus:
      // NativeHost 只在连不上桌面端时发这条，随后自己退出。
      console.info("AM:", message.error?.message ?? message);
      setBadge(false);
      break;

    case Types.CollectStart:
      start(message.payload);
      break;

    case Types.CollectStop:
      if (current?.batchId === message.payload?.batchId) {
        current.controller.abort();
      }
      break;

    default:
      console.warn("AM: 未知消息", message.type);
  }
}

async function start(payload) {
  const { batchId } = payload;
  if (current) {
    send(Types.CollectFailed, { batchId }, { code: "BUSY", message: `已有采集在进行：${current.batchId}` });
    return;
  }

  const controller = new AbortController();
  current = { batchId, controller };
  send(Types.CollectAccepted, { batchId });
  try {
    await run(payload, send, controller.signal);
  } catch (error) {
    if (controller.signal.aborted) {
      send(Types.CollectStopped, { batchId });
    } else {
      send(Types.CollectFailed, { batchId }, { code: "COLLECTOR_ERROR", message: String(error?.message ?? error) });
    }
  } finally {
    if (current?.controller === controller) {
      current = null;
    }
  }
}

function setBadge(connected) {
  chrome.action.setBadgeText({ text: connected ? "" : "!" });
  chrome.action.setTitle({ title: connected ? "AM 采集：已连接" : "AM 采集：未连接桌面端" });
}

// 浏览器启动、插件安装和每次重连闹钟都尝试连接。闹钟最短间隔 30 秒。
chrome.runtime.onStartup.addListener(connect);
chrome.runtime.onInstalled.addListener(connect);
chrome.alarms.onAlarm.addListener((alarm) => {
  if (alarm.name === RECONNECT_ALARM) {
    connect();
  }
});
chrome.alarms.get(RECONNECT_ALARM).then((alarm) => {
  if (!alarm) {
    chrome.alarms.create(RECONNECT_ALARM, { periodInMinutes: 0.5 });
  }
});
chrome.action.onClicked.addListener(connect);
connect();
