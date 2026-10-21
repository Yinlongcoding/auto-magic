// 通信骨架用的假采集：不访问任何网页，按协议产出假商品，用来验证两遍抓取、健康检查和停止。
// 接入真实 1688 采集时整个文件删除，换成真实采集器，对外接口（run 的参数和 emit 的消息）保持不变。

import { Types } from "./protocol.js";

// 关键词含这个标记时第 1 遍一半失败，用来触发"跳过第 2 遍"。只在骨架阶段存在。
const FORCE_UNHEALTHY_MARK = "#fail";

/**
 * 跑一个批次。emit(type, payload) 把消息发给桌面端。
 * signal 中止时抛 AbortError，调用方据此回 collect.stopped。
 */
export async function run(start, emit, signal) {
  const { batchId, keyword, targetCount, intervalMs, healthFailureRate } = start;
  const unhealthy = keyword.includes(FORCE_UNHEALTHY_MARK);

  // 第 1 遍：每 10 个失败 1 个，或强制一半失败。
  const failed = [];
  for (let index = 1; index <= targetCount; index++) {
    await sleep(intervalMs, signal);
    const success = unhealthy ? index % 2 === 0 : index % 10 !== 0;
    emit(Types.CollectItem, item(batchId, keyword, index, 1, success));
    emit(Types.CollectProgress, { batchId, pass: 1, done: index, total: targetCount });
    if (!success) {
      failed.push(index);
    }
  }

  const skipSecondPass = failed.length / targetCount > healthFailureRate;
  emit(Types.CollectPassDone, { batchId, pass: 1, total: targetCount, failed: failed.length, skipSecondPass });

  // 第 2 遍：只重抓第 1 遍失败的，假定全部成功。
  let stillFailed = failed.length;
  if (!skipSecondPass && failed.length > 0) {
    stillFailed = 0;
    for (const [i, index] of failed.entries()) {
      await sleep(intervalMs, signal);
      emit(Types.CollectItem, item(batchId, keyword, index, 2, true));
      emit(Types.CollectProgress, { batchId, pass: 2, done: i + 1, total: failed.length });
    }
    emit(Types.CollectPassDone, { batchId, pass: 2, total: failed.length, failed: 0, skipSecondPass: false });
  }

  emit(Types.CollectFinished, {
    batchId,
    succeeded: targetCount - stillFailed,
    failed: stillFailed,
    healthy: !skipSecondPass,
  });
}

function item(batchId, keyword, index, pass, success) {
  return success
    ? { batchId, index, pass, success, offerId: `fake-${index}`, title: `${keyword} 假商品 ${index}`, skuCount: 1 + (index % 4), failureCode: null }
    : { batchId, index, pass, success, offerId: null, title: null, skuCount: 0, failureCode: "FAKE_FAILURE" };
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
