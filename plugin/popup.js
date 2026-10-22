// 探测弹窗。页面函数不能直接跨世界传递，所以注入的是函数源码，再把结果取回来。
// 连接状态问后台，以后台是否收到过桌面端的 bridge.pong 为准。
import { probePage } from "./probe.js";

const probeButton = document.getElementById("probe");
const downloadButton = document.getElementById("download");
const status = document.getElementById("status");
let lastResult = null;

async function showConnectionState() {
  const state = await chrome.runtime.sendMessage({ kind: "probe.state" });
  if (state?.connected) {
    status.textContent = "桌面端已连接。在 1688 搜索结果页或商品详情页点击探测。";
    return true;
  }

  status.textContent = state?.hasPort
    ? "正在连接 AM 桌面端…等一下再点探测。"
    : "AM 桌面端未连接：请先启动 AM，再点这里。";
  return false;
}

probeButton.addEventListener("click", async () => {
  probeButton.disabled = true;
  status.textContent = "正在读取当前页面…";
  try {
    const [tab] = await chrome.tabs.query({ active: true, currentWindow: true });
    if (!tab?.id) {
      throw new Error("找不到当前标签页。");
    }

    // MAIN：在页面主环境运行，才能读到页面自己的全局变量（如 window.context）。只读，不改页面。
    const [{ result }] = await chrome.scripting.executeScript({
      target: { tabId: tab.id },
      world: "MAIN",
      func: probePage,
    });
    lastResult = result;
    downloadButton.disabled = false;

    const reply = await chrome.runtime.sendMessage({ kind: "probe.result", payload: result });
    status.textContent = reply?.sent
      ? `已发送到 AM。页面类型：${result.pageKind}`
      : `没有发送：${reply?.connected === false ? "AM 桌面端未连接" : "与桌面端的连接已断开"}。结果可另存为文件。\n页面类型：${result.pageKind}`;
  } catch (error) {
    status.textContent = `探测失败：${error.message}`;
  } finally {
    probeButton.disabled = false;
  }
});

downloadButton.addEventListener("click", () => {
  if (!lastResult) {
    return;
  }

  const host = lastResult.host.replaceAll(".", "-");
  const blob = new Blob([JSON.stringify(lastResult, null, 2)], { type: "application/json" });
  const url = URL.createObjectURL(blob);
  const anchor = document.createElement("a");
  anchor.href = url;
  anchor.download = `am-probe-${host}-${Date.now()}.json`;
  anchor.click();
  URL.revokeObjectURL(url);
});

showConnectionState();
