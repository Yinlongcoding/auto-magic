// 1688 真实采集：搜索页收集商品 ID，再逐个打开详情页读取数据。
// 用一个专用标签页串行抓取，按 intervalMs 间隔。规则见设计目录 16 号。
// 对外接口：run(start, emit, signal)，emit(type, payload) 发消息给桌面端，signal 中止时抛 AbortError。

import { Types } from "./protocol.js";
import { readDetailPage, readSearchPage } from "./page-readers.js";

const SEARCH_URL = "https://s.1688.com/selloffer/offer_search.htm";
const NAVIGATION_TIMEOUT_MS = 30000;
const SEARCH_SCROLL_MS = 20000;
const DETAIL_WAIT_MS = 8000;
const MAX_SEARCH_PAGES = 20;
// 连续这么多个详情页落到登录或验证页，就整批停下，避免继续访问加重风控
const MAX_CONSECUTIVE_RISK = 3;
// Chrome 规定发给 NativeHost 的单条消息不超过 1 MB，留出余量
const MAX_ITEM_CHARS = 900_000;

/** 采集级别的失败：整批停下，回 collect.failed。code 和 message 会显示给用户。 */
export class CollectorError extends Error {
  constructor(code, message) {
    super(message);
    this.code = code;
  }
}

export async function run(start, emit, signal) {
  const { batchId, targetCount, intervalMs, healthFailureRate } = start;
  const tab = await chrome.tabs.create({ url: "about:blank", active: true });
  try {
    // 搜索页卡片靠滚动懒加载，后台标签页可能不触发，所以采集标签页设为当前页
    const offerIds = await collectOfferIds(tab.id, start, signal);
    if (offerIds.length === 0) {
      throw new CollectorError("NO_RESULTS", "搜索结果为空，或页面上没有可采集的商品。");
    }

    const total = offerIds.length;
    const risk = { consecutive: 0 };
    const visit = async (offerId, index, pass) => {
      const item = await collectOne(tab.id, offerId, signal);
      risk.consecutive = item.failureCode === "RISK_PAGE" ? risk.consecutive + 1 : 0;
      emit(Types.CollectItem, { batchId, index, pass, ...item });
      if (risk.consecutive >= MAX_CONSECUTIVE_RISK) {
        throw new CollectorError("RISK_STOP", "连续多个页面要求登录或验证，已停止。请在浏览器里处理后重新采集。");
      }
      return item.success;
    };

    // 第 1 遍
    const failed = [];
    for (const [i, offerId] of offerIds.entries()) {
      await sleep(i === 0 ? 0 : intervalMs, signal);
      if (!(await visit(offerId, i + 1, 1))) {
        failed.push({ offerId, index: i + 1 });
      }
      emit(Types.CollectProgress, { batchId, pass: 1, done: i + 1, total });
    }

    const skipSecondPass = failed.length / total > healthFailureRate;
    emit(Types.CollectPassDone, { batchId, pass: 1, total, failed: failed.length, skipSecondPass });

    // 第 2 遍：只重抓第 1 遍失败的
    let stillFailed = failed.length;
    if (!skipSecondPass && failed.length > 0) {
      stillFailed = 0;
      for (const [i, { offerId, index }] of failed.entries()) {
        await sleep(intervalMs, signal);
        if (!(await visit(offerId, index, 2))) {
          stillFailed++;
        }
        emit(Types.CollectProgress, { batchId, pass: 2, done: i + 1, total: failed.length });
      }
      emit(Types.CollectPassDone, { batchId, pass: 2, total: failed.length, failed: stillFailed, skipSecondPass: false });
    }

    emit(Types.CollectFinished, { batchId, succeeded: total - stillFailed, failed: stillFailed, healthy: !skipSecondPass });
  } finally {
    await chrome.tabs.remove(tab.id).catch(() => {});
  }
}

/** 按页打开搜索结果，收集普通卡片的商品 ID，直到够数或没有新商品。 */
async function collectOfferIds(tabId, start, signal) {
  const ids = [];
  for (let page = 1; page <= MAX_SEARCH_PAGES && ids.length < start.targetCount; page++) {
    if (page > 1) {
      await sleep(start.intervalMs, signal);
    }

    const url = buildSearchUrl(start, page);
    const landed = await navigate(tabId, url, signal);
    if (isRiskUrl(landed)) {
      throw new CollectorError("RISK_STOP", "搜索页要求登录或验证。请在浏览器里处理后重新采集。");
    }

    const [{ result }] = await chrome.scripting.executeScript({
      target: { tabId },
      func: readSearchPage,
      args: [SEARCH_SCROLL_MS],
    });
    const fresh = result.offerIds.filter((id) => !ids.includes(id));
    console.info(`AM: 搜索第 ${page} 页，卡片 ${result.cardCount}，广告 ${result.adCount}，新商品 ${fresh.length}`);
    if (fresh.length === 0) {
      break;
    }
    ids.push(...fresh);
  }
  return ids.slice(0, start.targetCount);
}

/** 抓一个详情页。单个商品的问题返回失败结果；标签页被关、停止等整批问题照常抛出。 */
async function collectOne(tabId, offerId, signal) {
  const url = `https://detail.1688.com/offer/${offerId}.html`;
  let landed;
  try {
    landed = await navigate(tabId, url, signal);
  } catch (error) {
    if (error instanceof NavigationTimeout) {
      return failure(offerId, "TIMEOUT");
    }
    throw error;
  }

  if (isRiskUrl(landed)) {
    return failure(offerId, "RISK_PAGE");
  }

  let result;
  try {
    [{ result }] = await chrome.scripting.executeScript({
      target: { tabId },
      world: "MAIN",
      func: readDetailPage,
      args: [offerId, DETAIL_WAIT_MS],
    });
  } catch (error) {
    // 错误页、跳到了不在站点权限内的地址等，注入会失败
    console.warn("AM: 详情页读取失败", offerId, error);
    return failure(offerId, "INJECT_FAILED");
  }

  if (!result?.offer) {
    return failure(offerId, result?.failureCode ?? "NO_PAGE_DATA");
  }

  const item = { success: true, offerId, failureCode: null, offer: result.offer };
  if (JSON.stringify(item).length > MAX_ITEM_CHARS) {
    return failure(offerId, "TOO_LARGE");
  }
  return item;
}

function failure(offerId, failureCode) {
  return { success: false, offerId, failureCode, offer: null };
}

function buildSearchUrl({ keyword, sort, purchasePriceMin, purchasePriceMax }, page) {
  const params = new URLSearchParams({ keywords: keyword, charset: "utf8" });
  if (sort === "sales") {
    params.set("sortType", "va_sales360");
    params.set("descendOrder", "true");
  }
  if (purchasePriceMin != null) params.set("priceStart", String(purchasePriceMin));
  if (purchasePriceMax != null) params.set("priceEnd", String(purchasePriceMax));
  params.set("beginPage", String(page));
  return `${SEARCH_URL}?${params}`;
}

function isRiskUrl(url) {
  return /login\.1688\.com|login\.taobao\.com|punish|x5sec|\/_____tmd_____|captcha/i.test(url ?? "");
}

class NavigationTimeout extends Error {}

/** 导航并等到加载完成，返回最终地址（可能被重定向）。先看到 loading 再认 complete，避免误认上一页。 */
function navigate(tabId, url, signal) {
  return new Promise((resolve, reject) => {
    let sawLoading = false;
    const timer = setTimeout(() => finish(() => reject(new NavigationTimeout(url))), NAVIGATION_TIMEOUT_MS);

    function onUpdated(id, change, tab) {
      if (id !== tabId) return;
      if (change.status === "loading") sawLoading = true;
      if (change.status === "complete" && sawLoading) finish(() => resolve(tab.url));
    }
    function onRemoved(id) {
      if (id === tabId) finish(() => reject(new CollectorError("TAB_CLOSED", "采集用的标签页被关闭了。")));
    }
    function onAbort() {
      finish(() => reject(signal.reason));
    }
    function finish(settle) {
      clearTimeout(timer);
      chrome.tabs.onUpdated.removeListener(onUpdated);
      chrome.tabs.onRemoved.removeListener(onRemoved);
      signal.removeEventListener("abort", onAbort);
      settle();
    }

    if (signal.aborted) {
      reject(signal.reason);
      return;
    }
    chrome.tabs.onUpdated.addListener(onUpdated);
    chrome.tabs.onRemoved.addListener(onRemoved);
    signal.addEventListener("abort", onAbort, { once: true });
    chrome.tabs.update(tabId, { url }).catch((error) => finish(() => reject(error)));
  });
}

function sleep(ms, signal) {
  return new Promise((resolve, reject) => {
    if (signal.aborted) {
      reject(signal.reason);
      return;
    }
    const timer = setTimeout(() => {
      signal.removeEventListener("abort", onAbort);
      resolve();
    }, ms);
    function onAbort() {
      clearTimeout(timer);
      reject(signal.reason);
    }
    signal.addEventListener("abort", onAbort, { once: true });
  });
}
