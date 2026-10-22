// 后台：连接 NativeHost，接收桌面端指令，同一时间只跑一个采集。
// 桌面端未启动时 NativeHost 回 bridge.status 错误并退出，这里定时重连。
// 连接状态以桌面端回过的 bridge.pong 为准：只有端口对象不代表桌面端还在。

import { HOST_NAME, Types, envelope } from "./protocol.js";
import { CollectorError, run } from "./collector.js";

const RECONNECT_ALARM = "reconnect";
const PING_INTERVAL_MS = 5000;
const PING_TIMEOUT_MS = 4000;

const RETRY_MIN_MS = 1000;
const RETRY_MAX_MS = 30000;

let port = null;
let current = null; // { batchId, controller }
// connected：桌面端真的回过 bridge.pong。NativeHost 进程可能已经退出，而端口对象还没触发断开。
let connected = false;
let pingTimer = null;
let pingDeadline = null;
let retryTimer = null;
let retryDelay = RETRY_MIN_MS;

function connect() {
  if (port) {
    return;
  }

  clearTimeout(retryTimer);
  retryTimer = null;
  port = chrome.runtime.connectNative(HOST_NAME);
  port.onMessage.addListener(onMessage);
  port.onDisconnect.addListener(() => {
    // 读一下 lastError，避免控制台报"未检查的错误"。
    const reason = chrome.runtime.lastError?.message;
    console.info("AM: 与桌面端断开", reason ?? "");
    port = null;
    markDisconnected();
    updateBadge();
    scheduleReconnect();
  });

  send(Types.ExtensionHello, { extensionVersion: chrome.runtime.getManifest().version, collector: "1688" });
  ping();
  startHeartbeat();
  updateBadge();
}

function send(type, payload, error) {
  if (!port) {
    return false;
  }

  port.postMessage(envelope(type, payload, error));
  return true;
}

function ping() {
  if (!port) {
    return;
  }

  clearTimeout(pingDeadline);
  pingDeadline = setTimeout(() => {
    // 桌面端没回 pong：主动断开重连，比等浏览器发现进程退出更可靠。
    console.info("AM: 探活超时，重新连接");
    const stale = port;
    port = null;
    stale?.disconnect();
    markDisconnected();
    updateBadge();
  }, PING_TIMEOUT_MS);
  send(Types.BridgePing, {});
}

function startHeartbeat() {
  clearInterval(pingTimer);
  pingTimer = setInterval(ping, PING_INTERVAL_MS);
}

/// 断开后按 1、2、4 … 30 秒退避重连；连上（收到 pong）后重置。
function scheduleReconnect() {
  clearTimeout(retryTimer);
  retryTimer = setTimeout(() => {
    retryTimer = null;
    if (!port) {
      connect();
    }
  }, retryDelay);
  retryDelay = Math.min(retryDelay * 2, RETRY_MAX_MS);
}

function markDisconnected() {
  connected = false;
  clearInterval(pingTimer);
  pingTimer = null;
  clearTimeout(pingDeadline);
  pingDeadline = null;
  current?.controller.abort();
  current = null;
}

function updateBadge() {
  chrome.action.setBadgeText({ text: connected ? "" : "!" });
  chrome.action.setTitle({
    title: connected ? "AM 采集：已连接" : port ? "AM 采集：连接中" : "AM 采集：未连接桌面端",
  });
}

// 探测弹窗发来的结果，原样转发给桌面端。仅开发阶段。
chrome.runtime.onMessage.addListener((message, _sender, sendResponse) => {
  if (message?.kind === "probe.state") {
    sendResponse({ connected, hasPort: port !== null });
    return false;
  }

  if (message?.kind !== "probe.result") {
    return false;
  }

  const sent = send(Types.ProbeResult, message.payload);
  sendResponse({ sent, connected });
  return false;
});

function onMessage(message) {
  switch (message.type) {
    case Types.BridgePong:
      clearTimeout(pingDeadline);
      pingDeadline = null;
      if (!connected) {
        connected = true;
        retryDelay = RETRY_MIN_MS;
        updateBadge();
      }
      break;

    case Types.BridgeStatus:
      // NativeHost 只在连不上桌面端时发这条，随后自己退出。
      console.info("AM:", message.error?.message ?? message);
      markDisconnected();
      updateBadge();
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
      const code = error instanceof CollectorError ? error.code : "COLLECTOR_ERROR";
      console.error("AM: 采集失败", error);
      send(Types.CollectFailed, { batchId }, { code, message: String(error?.message ?? error) });
    }
  } finally {
    if (current?.controller === controller) {
      current = null;
    }
  }
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
connect();
