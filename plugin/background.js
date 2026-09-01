import {
  DEBUGGER_PROTOCOL_VERSION,
  DETAIL_DOM_OPTIONS,
  HOME_URL,
  LIMITS,
  SELECTORS,
  SORT_MODES,
  areSearchKeywordsEqual,
  isAllowed1688Url,
  isSearchResultUrl,
  normalizeStartRequest,
  selectReusableSearchResultTab,
} from './lib/config.js';
import {
  NATIVE_HOST_NAME,
  NATIVE_MESSAGE_TYPES,
  createNativeEnvelope,
  normalizeDesktopSearchEnvelope,
} from './lib/native-protocol.js';

let activeJob = null;
let creatingOffscreenDocument = null;
let nativePort = null;
let nativeReconnectTimer = null;

const NATIVE_RECONNECT_ALARM = 'auto-magic-native-reconnect';
const NATIVE_RECONNECT_DELAY_MS = 15_000;

chrome.runtime.onInstalled.addListener(() => {
  void ensureNativeReconnectAlarm();
  connectNativeHost();
});

chrome.runtime.onStartup.addListener(() => {
  void ensureNativeReconnectAlarm();
  connectNativeHost();
});

chrome.alarms.onAlarm.addListener((alarm) => {
  if (alarm.name === NATIVE_RECONNECT_ALARM) connectNativeHost();
});

void ensureNativeReconnectAlarm();
connectNativeHost();

chrome.runtime.onMessage.addListener((message, _sender, sendResponse) => {
  if (message?.type === 'GET_DEMO_STATE') {
    chrome.storage.local.get(['demoState']).then(({ demoState }) => {
      sendResponse(demoState ?? { status: 'idle' });
    });
    return true;
  }

  if (message?.type === 'START_1688_DOM_DEMO') {
    if (activeJob) {
      sendResponse({ accepted: false, error: '已有采集任务正在执行。' });
      return false;
    }

    try {
      const request = normalizeStartRequest(message);
      const jobId = crypto.randomUUID();
      const job = {
        jobId,
        createdTabIds: new Set(),
        attachedTabIds: new Set(),
        temporaryTabIds: new Set(),
      };
      activeJob = job;
      void executeSearchJob(job, request);
      sendResponse({ accepted: true, jobId });
    } catch (error) {
      sendResponse({ accepted: false, error: error.message });
    }
    return false;
  }

  if (message?.type === 'SHOW_DETAIL_DOM_DOWNLOAD') {
    void showLatestDetailDomDownload()
      .then(() => sendResponse({ success: true }))
      .catch((error) => sendResponse({ success: false, error: error.message }));
    return true;
  }

  return false;
});

async function executeSearchJob(job, request) {
  try {
    const result = await runSearchJob(job, request);
    if (job.desktopRequestId) {
      postNativeMessage(createNativeEnvelope(
        NATIVE_MESSAGE_TYPES.searchCompleted,
        job.desktopRequestId,
        result.data,
      ));
    }
  } catch (error) {
    try {
      await finishWithError(job, error);
    } catch (reportError) {
      console.error('记录采集任务失败状态时发生错误。', reportError);
    }
    if (job.desktopRequestId) {
      postNativeMessage(createNativeEnvelope(
        NATIVE_MESSAGE_TYPES.searchFailed,
        job.desktopRequestId,
        undefined,
        { code: 'SEARCH_FAILED', message: error.message },
      ));
    }
  } finally {
    await cleanupJob(job);
  }
}

async function runSearchJob(job, request) {
  await updateState({
    status: 'running',
    jobId: job.jobId,
    keyword: request.keyword,
    step: '正在检查当前窗口的1688搜索页',
    startedAt: new Date().toISOString(),
    completedAt: null,
    result: null,
  });
  await setBadge('…', '#F97316');

  const resultTab = await resolveSearchResultTab(job, request);
  assertTabId(resultTab.id);

  await chrome.tabs.update(resultTab.id, { active: true });
  await waitForTabComplete(resultTab.id, LIMITS.pageLoadTimeoutMs);
  await attachDebugger(job, resultTab.id);
  await waitForDomSelector(
    resultTab.id,
    SELECTORS.productCard,
    LIMITS.productRenderTimeoutMs,
  );
  await jumpToPageTop(resultTab.id);

  const filterAndSort = await applyProductFilterAndSort(resultTab.id, request, job);
  await jumpToPageTop(resultTab.id);
  await updateState({
    status: 'running',
    jobId: job.jobId,
    step: '正在完整加载筛选后的商品列表',
  });
  const scroll = await scrollToBottomForProducts(resultTab.id);

  await chrome.scripting.executeScript({
    target: { tabId: resultTab.id },
    files: ['content/extractor.js'],
  });
  const extraction = await chrome.tabs.sendMessage(resultTab.id, {
    type: 'EXTRACT_1688_PRODUCTS',
    maxItems: request.maxItems,
    selectors: SELECTORS,
  });
  if (!extraction?.success) {
    throw new Error(extraction?.error || '商品DOM解析失败。');
  }
  if (!extraction.data.count) {
    throw new Error('页面已打开，但没有识别到商品卡片。');
  }
  const detailUrlResolution = await resolveDynamicDetailUrls(
    resultTab.id,
    extraction.data,
    job,
  );
  await jumpToPageTop(resultTab.id);

  let detailDom;
  if (request.includeDetailDom) {
    await updateState({
      status: 'running',
      jobId: job.jobId,
      step: '正在隐藏获取列表第2条商品的详情DOM',
    });
    detailDom = await captureDefaultDetailDom(
      extraction.data.items,
      true,
    );
  }

  let detailSnapshot;
  if (request.includeDetailFacts) {
    await updateState({
      status: 'running',
      jobId: job.jobId,
      step: '正在从真实Chrome详情页采集列表第2条商品',
    });
    const detailCapture = await captureDefaultRenderedDetail(job, extraction.data.items);
    if (detailCapture.success) {
      detailSnapshot = {
        detailUrl: detailCapture.finalUrl,
        capturedAt: detailCapture.capturedAt,
        pageTitle: detailCapture.pageTitle,
        facts: detailCapture.facts ?? [],
        diagnostics: detailCapture.factDiagnostics ?? null,
        raw: detailCapture.raw ?? null,
      };
    } else {
      throw new Error(detailCapture.error || '第2条商品详情采集失败。');
    }
  }

  const result = {
    type: 'PRODUCT_SEARCH_RESULT',
    jobId: job.jobId,
    success: true,
    data: {
      keyword: request.keyword,
      ...extraction.data,
      ...(detailDom === undefined ? {} : { detailDom }),
      ...(detailSnapshot === undefined ? {} : { detailSnapshot }),
      diagnostics: {
        filterAndSort,
        scroll,
        detailUrlResolution,
        extraction: extraction.data.diagnostics,
      },
    },
  };
  await updateState({
    status: 'completed',
    jobId: job.jobId,
    keyword: request.keyword,
    step: formatCompletionStep(result.data),
    completedAt: new Date().toISOString(),
    result,
  });
  await setBadge(String(Math.min(result.data.count, 99)), '#16A34A');
  return result;
}

async function resolveSearchResultTab(job, request) {
  const currentWindowTabs = await chrome.tabs.query({ currentWindow: true });
  const reusableTab = selectReusableSearchResultTab(currentWindowTabs);

  if (reusableTab) {
    return reuseSearchResultTab(job, reusableTab, request);
  }

  return createSearchResultTab(job, request);
}

async function reuseSearchResultTab(job, reusableTab, request) {
  assertTabId(reusableTab.id);
  await chrome.tabs.update(reusableTab.id, { active: true });
  await waitForTabComplete(reusableTab.id, LIMITS.pageLoadTimeoutMs);
  await attachDebugger(job, reusableTab.id);
  await waitForDomSelector(
    reusableTab.id,
    SELECTORS.searchInput,
    LIMITS.pageLoadTimeoutMs,
  );

  const currentKeyword = await readSearchInputValue(reusableTab.id);
  if (areSearchKeywordsEqual(currentKeyword, request.keyword)) {
    await updateState({
      status: 'running',
      jobId: job.jobId,
      step: '已复用当前搜索结果，关键词一致，无需重新搜索',
    });
    return reusableTab;
  }

  await updateState({
    status: 'running',
    jobId: job.jobId,
    step: '已复用当前搜索页，正在替换关键词',
  });
  return replaceKeywordAndSearch(reusableTab.id, request.keyword, job);
}

async function createSearchResultTab(job, request) {
  await updateState({
    status: 'running',
    jobId: job.jobId,
    step: '当前窗口没有搜索结果页，正在打开1688首页',
  });
  const sourceTab = await chrome.tabs.create({ url: HOME_URL, active: true });
  assertTabId(sourceTab.id);
  job.createdTabIds.add(sourceTab.id);

  await waitForTabComplete(sourceTab.id, LIMITS.pageLoadTimeoutMs);
  await attachDebugger(job, sourceTab.id);
  await waitForDomSelector(sourceTab.id, SELECTORS.searchInput, LIMITS.pageLoadTimeoutMs);

  await updateState({ status: 'running', jobId: job.jobId, step: '正在输入搜索词' });
  return replaceKeywordAndSearch(sourceTab.id, request.keyword, job);
}

async function replaceKeywordAndSearch(sourceTabId, keyword, job) {
  await focusSearchInput(sourceTabId);
  await sendDebuggerCommand(sourceTabId, 'Input.insertText', { text: keyword });

  const resultTabPromise = waitForSearchResultTab(
    sourceTabId,
    LIMITS.searchResultTimeoutMs,
  );
  await updateState({ status: 'running', jobId: job.jobId, step: '正在按下Enter搜索' });
  await pressEnter(sourceTabId);

  const resultTab = await resultTabPromise;
  assertTabId(resultTab.id);
  if (resultTab.id !== sourceTabId) job.createdTabIds.add(resultTab.id);
  return resultTab;
}

async function finishWithError(job, error) {
  await updateState({
    status: 'failed',
    jobId: job.jobId,
    step: error.message,
    completedAt: new Date().toISOString(),
  });
  await setBadge('!', '#DC2626');
}

async function cleanupJob(job) {
  for (const tabId of job.attachedTabIds) {
    try {
      await chrome.debugger.detach({ tabId });
    } catch {
      // 标签页可能已被用户关闭或调试器已主动断开。
    }
  }
  job.attachedTabIds.clear();
  for (const tabId of job.temporaryTabIds ?? []) {
    try {
      await chrome.tabs.remove(tabId);
    } catch {
      // 临时详情标签页可能已经在采集结束时关闭。
    }
  }
  job.temporaryTabIds?.clear();
  if (activeJob?.jobId === job.jobId) activeJob = null;
}

async function attachDebugger(job, tabId) {
  if (job.attachedTabIds.has(tabId)) return;
  await chrome.debugger.attach({ tabId }, DEBUGGER_PROTOCOL_VERSION);
  job.attachedTabIds.add(tabId);
}

function sendDebuggerCommand(tabId, method, commandParams = {}) {
  return chrome.debugger.sendCommand({ tabId }, method, commandParams);
}

async function readSearchInputValue(tabId) {
  const result = await sendDebuggerCommand(tabId, 'Runtime.evaluate', {
    expression: `(() => {
      const input = document.querySelector(${JSON.stringify(SELECTORS.searchInput)});
      return typeof input?.value === 'string' ? input.value : null;
    })()`,
    returnByValue: true,
  });
  const value = result?.result?.value;
  if (typeof value !== 'string') throw new Error('没有读取到1688搜索框内容。');
  return value;
}

async function focusSearchInput(tabId) {
  const result = await sendDebuggerCommand(tabId, 'Runtime.evaluate', {
    expression: `(() => {
      const input = document.querySelector(${JSON.stringify(SELECTORS.searchInput)});
      if (!input) return false;
      input.focus();
      input.select?.();
      return true;
    })()`,
    returnByValue: true,
  });
  if (!result?.result?.value) throw new Error('没有找到1688搜索框。');
}

async function pressEnter(tabId) {
  const key = {
    key: 'Enter',
    code: 'Enter',
    windowsVirtualKeyCode: 13,
    nativeVirtualKeyCode: 13,
  };
  await sendDebuggerCommand(tabId, 'Input.dispatchKeyEvent', {
    type: 'rawKeyDown',
    ...key,
  });
  await sendDebuggerCommand(tabId, 'Input.dispatchKeyEvent', {
    type: 'keyUp',
    ...key,
  });
}

async function waitForDomSelector(tabId, selector, timeoutMs) {
  const startedAt = Date.now();
  while (Date.now() - startedAt < timeoutMs) {
    const result = await sendDebuggerCommand(tabId, 'Runtime.evaluate', {
      expression: `Boolean(document.querySelector(${JSON.stringify(selector)}))`,
      returnByValue: true,
    });
    if (result?.result?.value) return;
    await delay(300);
  }
  throw new Error(`等待页面元素超时：${selector}`);
}

async function applyProductFilterAndSort(tabId, request, job) {
  let priceFilter = { applied: false };
  if (
    request.procurementMinimumCny !== null &&
    request.procurementMaximumCny !== null
  ) {
    await updateState({
      status: 'running',
      jobId: job.jobId,
      step: `正在筛选采购价 ${formatPrice(request.procurementMinimumCny)} - ${formatPrice(request.procurementMaximumCny)} CNY`,
    });
    priceFilter = await applyPriceFilter(
      tabId,
      request.procurementMinimumCny,
      request.procurementMaximumCny,
    );
    const reload = priceFilter.clicked
      ? await waitForProductListReload(
        tabId,
        priceFilter.reloadMarker,
        LIMITS.productRenderTimeoutMs,
      )
      : {
        completed: false,
        skipped: true,
        reason: 'price-filter-already-applied',
      };
    priceFilter = await verifyPriceFilterState(
      tabId,
      request.procurementMinimumCny,
      request.procurementMaximumCny,
      { ...priceFilter, reload },
    );
  }

  await updateState({
    status: 'running',
    jobId: job.jobId,
    step: request.sortMode === SORT_MODES.priceAscending
      ? '正在切换为价格优先'
      : '正在切换为销量优先',
  });
  const sortReloadMarker = await markProductListState(tabId);
  let sorting = await applySortMode(tabId, request.sortMode);
  if (sorting.clicked) {
    sorting = {
      ...sorting,
      reload: await waitForProductListReload(
        tabId,
        sortReloadMarker,
        LIMITS.productRenderTimeoutMs,
      ),
    };
  }
  await waitForDomSelector(tabId, SELECTORS.productCard, LIMITS.productRenderTimeoutMs);

  return {
    priceFilter,
    sorting,
  };
}

async function applyPriceFilter(tabId, minimumCny, maximumCny) {
  const reloadMarker = await markProductListState(tabId);
  const valuesResult = await sendDebuggerCommand(tabId, 'Runtime.evaluate', {
    expression: `(() => {
      const activeForms = [
        ...document.querySelectorAll(${JSON.stringify(SELECTORS.activePriceFilterForm)})
      ];
      const allForms = [
        ...document.querySelectorAll(${JSON.stringify(SELECTORS.priceFilterForm)})
      ];
      const forms = [
        ...activeForms,
        ...allForms.filter((candidate) => !activeForms.includes(candidate)),
      ];
      const isVisible = (element) => {
        if (!(element instanceof HTMLElement)) return false;
        const rect = element.getBoundingClientRect();
        const style = getComputedStyle(element);
        return rect.width > 0 && rect.height > 0 &&
          style.display !== 'none' && style.visibility !== 'hidden' &&
          style.opacity !== '0';
      };
      const form = forms.find((candidate) => {
        const minimumInput = candidate.querySelector('input[placeholder*="最低价"]');
        const maximumInput = candidate.querySelector('input[placeholder*="最高价"]');
        return isVisible(minimumInput) && isVisible(maximumInput);
      });
      if (!form) return { success: false, error: '没有找到价格筛选表单。' };

      const minimumInput = form.querySelector(${JSON.stringify(SELECTORS.priceMinimumInput)});
      const maximumInput = form.querySelector(${JSON.stringify(SELECTORS.priceMaximumInput)});
      if (!(minimumInput instanceof HTMLInputElement) ||
          !(maximumInput instanceof HTMLInputElement)) {
        return { success: false, error: '没有找到价格筛选上下限输入框。' };
      }

      const setValue = Object.getOwnPropertyDescriptor(
        HTMLInputElement.prototype,
        'value'
      )?.set;
      if (!setValue) return { success: false, error: '无法设置价格筛选输入框。' };
      const expectedMinimum = ${JSON.stringify(formatPrice(minimumCny))};
      const expectedMaximum = ${JSON.stringify(formatPrice(maximumCny))};
      const valuesMatch = (actual, expected) =>
        String(actual ?? '').trim() !== '' && Number(actual) === Number(expected);
      const assign = (input, value) => {
        setValue.call(input, value);
        input.dispatchEvent(new Event('input', { bubbles: true }));
        input.dispatchEvent(new Event('change', { bubbles: true }));
      };
      const previousMinimumValue = minimumInput.value;
      const previousMaximumValue = maximumInput.value;
      const currentUrl = new URL(location.href);
      const alreadyApplied =
        valuesMatch(previousMinimumValue, expectedMinimum) &&
        valuesMatch(previousMaximumValue, expectedMaximum) &&
        valuesMatch(currentUrl.searchParams.get('priceStart'), expectedMinimum) &&
        valuesMatch(currentUrl.searchParams.get('priceEnd'), expectedMaximum);
      if (!alreadyApplied) assign(minimumInput, expectedMinimum);
      return {
        success: true,
        alreadyApplied,
      };
    })()`,
    returnByValue: true,
  });
  const values = valuesResult?.result?.value;
  if (!values?.success) {
    throw new Error(values?.error || '填写采购成本区间失败。');
  }

  if (values.alreadyApplied) {
    return {
      applied: true,
      clicked: false,
      alreadyApplied: true,
      minimumCny,
      maximumCny,
      reloadMarker,
    };
  }

  await clickPriceMaximumInput(tabId, minimumCny);
  const maximumResult = await sendDebuggerCommand(tabId, 'Runtime.evaluate', {
    expression: `(() => {
      const expectedMinimum = ${JSON.stringify(formatPrice(minimumCny))};
      const isVisible = (element) => {
        if (!(element instanceof HTMLElement)) return false;
        const rect = element.getBoundingClientRect();
        const style = getComputedStyle(element);
        return rect.width > 0 && rect.height > 0 &&
          style.display !== 'none' && style.visibility !== 'hidden' &&
          style.opacity !== '0';
      };
      const activeForms = [
        ...document.querySelectorAll(${JSON.stringify(SELECTORS.activePriceFilterForm)})
      ];
      const allForms = [
        ...document.querySelectorAll(${JSON.stringify(SELECTORS.priceFilterForm)})
      ];
      const forms = [
        ...activeForms,
        ...allForms.filter((candidate) => !activeForms.includes(candidate)),
      ];
      const form = forms.find((candidate) => {
        const minimumInput = candidate.querySelector(
          ${JSON.stringify(SELECTORS.priceMinimumInput)}
        );
        const maximumInput = candidate.querySelector(
          ${JSON.stringify(SELECTORS.priceMaximumInput)}
        );
        return isVisible(minimumInput) && isVisible(maximumInput) &&
          Number(minimumInput.value) === Number(expectedMinimum);
      });
      const maximumInput = form?.querySelector(${JSON.stringify(SELECTORS.priceMaximumInput)});
      if (!(maximumInput instanceof HTMLInputElement)) {
        return { success: false, error: '没有找到最高采购价输入框。' };
      }
      const setValue = Object.getOwnPropertyDescriptor(
        HTMLInputElement.prototype,
        'value'
      )?.set;
      if (!setValue) return { success: false, error: '无法设置最高采购价。' };
      setValue.call(maximumInput, ${JSON.stringify(formatPrice(maximumCny))});
      maximumInput.dispatchEvent(new Event('input', { bubbles: true }));
      maximumInput.dispatchEvent(new Event('change', { bubbles: true }));
      maximumInput.focus({ preventScroll: true });
      return {
        success: true,
        maximumValue: maximumInput.value,
        maximumInputFocused: document.activeElement === maximumInput,
      };
    })()`,
    returnByValue: true,
  });
  const maximum = maximumResult?.result?.value;
  if (!maximum?.success) {
    throw new Error(maximum?.error || '填写最高采购价失败。');
  }
  if (!maximum.maximumInputFocused) {
    throw new Error('最高采购价已填写，但输入框未能保持焦点。');
  }

  await waitForClickablePriceConfirmButton(
    tabId,
    LIMITS.filterControlTimeoutMs,
    minimumCny,
    maximumCny,
  );
  const confirmClick = await clickFocusedPriceConfirmButton(
    tabId,
    minimumCny,
    maximumCny,
  );

  return {
    applied: true,
    clicked: true,
    alreadyApplied: false,
    maximumInputFocused: confirmClick.maximumInputFocused,
    minimumCny,
    maximumCny,
    reloadMarker,
  };
}

async function clickPriceMaximumInput(tabId, minimumCny) {
  const pointResult = await sendDebuggerCommand(tabId, 'Runtime.evaluate', {
    expression: `(() => {
      const expectedMinimum = ${JSON.stringify(formatPrice(minimumCny))};
      const activeForms = [
        ...document.querySelectorAll(${JSON.stringify(SELECTORS.activePriceFilterForm)})
      ];
      const allForms = [
        ...document.querySelectorAll(${JSON.stringify(SELECTORS.priceFilterForm)})
      ];
      const forms = [
        ...activeForms,
        ...allForms.filter((candidate) => !activeForms.includes(candidate)),
      ];
      const isVisible = (element) => {
        if (!(element instanceof HTMLElement)) return false;
        const rect = element.getBoundingClientRect();
        const style = getComputedStyle(element);
        return rect.width > 0 && rect.height > 0 &&
          style.display !== 'none' && style.visibility !== 'hidden' &&
          style.opacity !== '0';
      };
      const form = forms.find((candidate) => {
        const minimumInput = candidate.querySelector(
          ${JSON.stringify(SELECTORS.priceMinimumInput)}
        );
        const maximumInput = candidate.querySelector(
          ${JSON.stringify(SELECTORS.priceMaximumInput)}
        );
        return isVisible(minimumInput) && isVisible(maximumInput) &&
          Number(minimumInput.value) === Number(expectedMinimum);
      });
      const maximumInput = form?.querySelector(${JSON.stringify(SELECTORS.priceMaximumInput)});
      if (!(maximumInput instanceof HTMLInputElement)) return null;
      maximumInput.scrollIntoView({
        block: 'nearest',
        inline: 'nearest',
        behavior: 'instant',
      });
      const rect = maximumInput.getBoundingClientRect();
      if (rect.width <= 0 || rect.height <= 0) return null;
      return {
        x: Math.floor(rect.left + rect.width / 2),
        y: Math.floor(rect.top + rect.height / 2),
      };
    })()`,
    returnByValue: true,
  });
  const point = pointResult?.result?.value;
  if (!Number.isFinite(point?.x) || !Number.isFinite(point?.y)) {
    throw new Error('填写最高采购价后无法点击该输入框。');
  }

  await sendDebuggerCommand(tabId, 'Input.dispatchMouseEvent', {
    type: 'mousePressed',
    x: point.x,
    y: point.y,
    button: 'left',
    clickCount: 1,
  });
  await sendDebuggerCommand(tabId, 'Input.dispatchMouseEvent', {
    type: 'mouseReleased',
    x: point.x,
    y: point.y,
    button: 'left',
    clickCount: 1,
  });
}

async function clickFocusedPriceConfirmButton(tabId, minimumCny, maximumCny) {
  const pointResult = await sendDebuggerCommand(tabId, 'Runtime.evaluate', {
    expression: `(() => {
      const expectedMinimum = ${JSON.stringify(formatPrice(minimumCny))};
      const expectedMaximum = ${JSON.stringify(formatPrice(maximumCny))};
      const valuesMatch = (actual, expected) =>
        String(actual ?? '').trim() !== '' && Number(actual) === Number(expected);
      const isVisible = (element) => {
        if (!(element instanceof HTMLElement)) return false;
        const rect = element.getBoundingClientRect();
        const style = getComputedStyle(element);
        return rect.width > 0 && rect.height > 0 &&
          style.display !== 'none' && style.visibility !== 'hidden' &&
          style.opacity !== '0' && style.pointerEvents !== 'none';
      };
      const activeForms = [
        ...document.querySelectorAll(${JSON.stringify(SELECTORS.activePriceFilterForm)})
      ];
      const allForms = [
        ...document.querySelectorAll(${JSON.stringify(SELECTORS.priceFilterForm)})
      ];
      const forms = [
        ...activeForms,
        ...allForms.filter((candidate) => !activeForms.includes(candidate)),
      ];
      const form = forms.find((candidate) => {
        const minimumInput = candidate.querySelector(
          ${JSON.stringify(SELECTORS.priceMinimumInput)}
        );
        const maximumInput = candidate.querySelector(
          ${JSON.stringify(SELECTORS.priceMaximumInput)}
        );
        return isVisible(minimumInput) && isVisible(maximumInput) &&
          valuesMatch(minimumInput.value, expectedMinimum) &&
          valuesMatch(maximumInput.value, expectedMaximum);
      });
      const maximumInput = form?.querySelector(
        ${JSON.stringify(SELECTORS.priceMaximumInput)}
      );
      maximumInput?.focus({ preventScroll: true });
      const buttons = form
        ? [...form.querySelectorAll(${JSON.stringify(SELECTORS.priceConfirmButton)})]
        : [];
      const button = buttons.find((candidate) =>
        candidate.closest('.price-filter-define') &&
        candidate.textContent?.trim() === '确定' &&
        isVisible(candidate)
      );
      if (!(button instanceof HTMLElement)) {
        return {
          success: false,
          error: '没有在当前价格表单中找到可见的确定按钮。',
          formCount: allForms.length,
          activeFormCount: activeForms.length,
          matchedForm: Boolean(form),
          buttonCount: buttons.length,
          maximumInputFocused: document.activeElement === maximumInput,
        };
      }
      const rect = button.getBoundingClientRect();
      const x = Math.floor(rect.left + rect.width / 2);
      const y = Math.floor(rect.top + rect.height / 2);
      const topElement = document.elementFromPoint(x, y);
      if (!(topElement === button || button.contains(topElement))) {
        return {
          success: false,
          error: '价格筛选确定按钮中心点被其他元素覆盖。',
          formCount: allForms.length,
          activeFormCount: activeForms.length,
          matchedForm: Boolean(form),
          buttonCount: buttons.length,
          maximumInputFocused: document.activeElement === maximumInput,
          coveringTag: topElement?.tagName ?? null,
          coveringClass: topElement?.className ?? null,
        };
      }
      // 1688会在输入框失焦时隐藏按钮；阻止mousedown默认失焦，保证click完成。
      button.addEventListener('mousedown', (event) => {
        event.preventDefault();
      }, { capture: true, once: true });
      return {
        success: true,
        x,
        y,
        maximumInputFocused: document.activeElement === maximumInput,
      };
    })()`,
    returnByValue: true,
  });
  const point = pointResult?.result?.value;
  if (!point?.success || !Number.isFinite(point.x) || !Number.isFinite(point.y)) {
    const diagnostics = point
      ? `（forms=${point.formCount ?? 'unknown'}，` +
        `activeForms=${point.activeFormCount ?? 'unknown'}，` +
        `matchedForm=${point.matchedForm ?? 'unknown'}，` +
        `maximumFocused=${point.maximumInputFocused ?? 'unknown'}，` +
        `buttons=${point.buttonCount ?? 'unknown'}，` +
        `cover=${point.coveringTag ?? 'none'}.${point.coveringClass ?? ''}）`
      : '';
    throw new Error(`${point?.error || '价格筛选确认按钮不可见或无法点击。'}${diagnostics}`);
  }

  await sendDebuggerCommand(tabId, 'Input.dispatchMouseEvent', {
    type: 'mouseMoved',
    x: point.x,
    y: point.y,
    button: 'none',
  });
  await sendDebuggerCommand(tabId, 'Input.dispatchMouseEvent', {
    type: 'mousePressed',
    x: point.x,
    y: point.y,
    button: 'left',
    clickCount: 1,
  });
  await sendDebuggerCommand(tabId, 'Input.dispatchMouseEvent', {
    type: 'mouseReleased',
    x: point.x,
    y: point.y,
    button: 'left',
    clickCount: 1,
  });
  return point;
}

async function waitForClickablePriceConfirmButton(
  tabId,
  timeoutMs,
  minimumCny,
  maximumCny,
) {
  const startedAt = Date.now();
  let lastDiagnostics = null;
  while (Date.now() - startedAt < timeoutMs) {
    const result = await sendDebuggerCommand(tabId, 'Runtime.evaluate', {
      expression: `(() => {
        const expectedMinimum = ${JSON.stringify(formatPrice(minimumCny))};
        const expectedMaximum = ${JSON.stringify(formatPrice(maximumCny))};
        const valuesMatch = (actual, expected) =>
          String(actual ?? '').trim() !== '' && Number(actual) === Number(expected);
        const isVisible = (element) => {
          if (!(element instanceof HTMLElement)) return false;
          const rect = element.getBoundingClientRect();
          const style = getComputedStyle(element);
          return rect.width > 0 && rect.height > 0 &&
            style.display !== 'none' && style.visibility !== 'hidden' &&
            style.opacity !== '0' && style.pointerEvents !== 'none';
        };
        const activeForms = [
          ...document.querySelectorAll(${JSON.stringify(SELECTORS.activePriceFilterForm)})
        ];
        const allForms = [
          ...document.querySelectorAll(${JSON.stringify(SELECTORS.priceFilterForm)})
        ];
        const forms = [
          ...activeForms,
          ...allForms.filter((candidate) => !activeForms.includes(candidate)),
        ];
        const form = forms.find((candidate) => {
          const minimumInput = candidate.querySelector(
            ${JSON.stringify(SELECTORS.priceMinimumInput)}
          );
          const maximumInput = candidate.querySelector(
            ${JSON.stringify(SELECTORS.priceMaximumInput)}
          );
          return isVisible(minimumInput) && isVisible(maximumInput) &&
            valuesMatch(minimumInput.value, expectedMinimum) &&
            valuesMatch(maximumInput.value, expectedMaximum);
        });
        const maximumInput = form?.querySelector(
          ${JSON.stringify(SELECTORS.priceMaximumInput)}
        );
        maximumInput?.focus({ preventScroll: true });
        const buttons = form
          ? [...form.querySelectorAll(${JSON.stringify(SELECTORS.priceConfirmButton)})]
          : [];
        const button = buttons.find((candidate) =>
          candidate.closest('.price-filter-define') &&
          candidate.textContent?.trim() === '确定' &&
          isVisible(candidate)
        );
        const rect = button?.getBoundingClientRect();
        const x = rect ? Math.floor(rect.left + rect.width / 2) : null;
        const y = rect ? Math.floor(rect.top + rect.height / 2) : null;
        const topElement = Number.isFinite(x) && Number.isFinite(y)
          ? document.elementFromPoint(x, y)
          : null;
        const clickable = Boolean(
          button && (topElement === button || button.contains(topElement))
        );
        return {
          visible: Boolean(button),
          clickable,
          activeFormCount: activeForms.length,
          formCount: allForms.length,
          matchedForm: Boolean(form),
          buttonCount: buttons.length,
          maximumInputFocused: document.activeElement === maximumInput,
          coveringTag: clickable ? null : topElement?.tagName ?? null,
          coveringClass: clickable ? null : topElement?.className ?? null,
        };
      })()`,
      returnByValue: true,
    });
    lastDiagnostics = result?.result?.value ?? null;
    if (lastDiagnostics?.clickable) return lastDiagnostics;
    await delay(50);
  }

  throw new Error(
    '点击最高采购价输入框后，当前价格表单的确定按钮仍不可点击。' +
    `（forms=${lastDiagnostics?.formCount ?? 0}，` +
    `activeForms=${lastDiagnostics?.activeFormCount ?? 0}，` +
    `matchedForm=${Boolean(lastDiagnostics?.matchedForm)}，` +
    `maximumFocused=${Boolean(lastDiagnostics?.maximumInputFocused)}，` +
    `buttons=${lastDiagnostics?.buttonCount ?? 0}，` +
    `cover=${lastDiagnostics?.coveringTag ?? 'none'}.` +
    `${lastDiagnostics?.coveringClass ?? ''}）`,
  );
}

async function markProductListState(tabId) {
  const token = crypto.randomUUID();
  const result = await sendDebuggerCommand(tabId, 'Runtime.evaluate', {
    expression: `(() => {
      const firstCard = document.querySelector(${JSON.stringify(SELECTORS.productCard)});
      if (!(firstCard instanceof HTMLElement)) return null;
      firstCard.setAttribute('data-auto-magic-reload-marker', ${JSON.stringify(token)});
      return {
        token: ${JSON.stringify(token)},
        beforeUrl: location.href,
        beforeFirstCardHref: firstCard.querySelector('a[href]')?.href ?? null,
      };
    })()`,
    returnByValue: true,
  });
  const state = result?.result?.value;
  if (!state?.token) {
    throw new Error('列表尚未显示，无法执行筛选或排序。');
  }
  return state;
}

async function waitForProductListReload(tabId, marker, timeoutMs) {
  await delay(LIMITS.filterApplyDelayMs);
  const startedAt = Date.now();
  while (Date.now() - startedAt < timeoutMs) {
    try {
      const result = await sendDebuggerCommand(tabId, 'Runtime.evaluate', {
        expression: `(() => {
          const cards = document.querySelectorAll(${JSON.stringify(SELECTORS.productCard)});
          const firstCard = cards[0];
          return {
            count: cards.length,
            currentUrl: location.href,
            markerStillPresent: Boolean(document.querySelector(
              '[data-auto-magic-reload-marker="${marker.token}"]'
            )),
            currentFirstCardHref: firstCard?.querySelector('a[href]')?.href ?? null,
          };
        })()`,
        returnByValue: true,
      });
      const state = result?.result?.value ?? {};
      const firstCardChanged =
        state.currentFirstCardHref !== marker.beforeFirstCardHref;
      if (Number(state.count ?? 0) > 0 &&
          (!state.markerStillPresent || firstCardChanged)) {
        return {
          completed: true,
          elapsedMs: Date.now() - startedAt + LIMITS.filterApplyDelayMs,
          urlChanged: state.currentUrl !== marker.beforeUrl,
          firstCardChanged,
          previousCardReplaced: !state.markerStillPresent,
        };
      }
    } catch {
      // 页面导航期间执行上下文可能短暂销毁，继续等待新列表。
    }
    await delay(100);
  }

  throw new Error('筛选或排序已点击，但等待商品列表重新加载超时。');
}

async function verifyPriceFilterState(
  tabId,
  minimumCny,
  maximumCny,
  priceFilter,
) {
  const result = await sendDebuggerCommand(tabId, 'Runtime.evaluate', {
    expression: `(() => {
      const expectedMinimum = ${JSON.stringify(formatPrice(minimumCny))};
      const expectedMaximum = ${JSON.stringify(formatPrice(maximumCny))};
      const valuesMatch = (actual, expected) =>
        String(actual ?? '').trim() !== '' && Number(actual) === Number(expected);
      const forms = [...document.querySelectorAll(${JSON.stringify(SELECTORS.priceFilterForm)})];
      const matchingForm = forms.find((candidate) => {
        const minimumInput = candidate.querySelector(
          ${JSON.stringify(SELECTORS.priceMinimumInput)}
        );
        const maximumInput = candidate.querySelector(
          ${JSON.stringify(SELECTORS.priceMaximumInput)}
        );
        return valuesMatch(minimumInput?.value, expectedMinimum) &&
          valuesMatch(maximumInput?.value, expectedMaximum);
      });
      const currentUrl = new URL(location.href);
      return {
        inputsMatch: Boolean(matchingForm),
        urlParametersMatch:
          valuesMatch(currentUrl.searchParams.get('priceStart'), expectedMinimum) &&
          valuesMatch(currentUrl.searchParams.get('priceEnd'), expectedMaximum),
      };
    })()`,
    returnByValue: true,
  });
  const state = result?.result?.value ?? {};
  if (!state.urlParametersMatch) {
    throw new Error('价格筛选操作完成，但页面URL中的价格区间与目标值不一致。');
  }

  return {
    ...priceFilter,
    inputsMatch: Boolean(state.inputsMatch),
    urlParametersMatch: true,
  };
}

async function applySortMode(tabId, sortMode) {
  const result = await sendDebuggerCommand(tabId, 'Runtime.evaluate', {
    expression: `(() => {
      const sortMode = ${JSON.stringify(sortMode)};
      if (sortMode === ${JSON.stringify(SORT_MODES.priceAscending)}) {
        const ascending = document.querySelector(${JSON.stringify(SELECTORS.priceAscendingSort)});
        if (!(ascending instanceof HTMLElement)) {
          return { success: false, error: '没有找到价格升序按钮。' };
        }
        if (ascending.classList.contains('bottom-active')) {
          return { success: true, clicked: false, mode: sortMode, alreadyActive: true };
        }
        ascending.click();
        return { success: true, clicked: true, mode: sortMode, alreadyActive: false };
      }

      const items = [...document.querySelectorAll(${JSON.stringify(SELECTORS.sortItem)})];
      const sales = items.find((item) => item.textContent?.trim() === '销量') ?? items[1];
      if (!(sales instanceof HTMLElement)) {
        return { success: false, error: '没有找到销量排序按钮。' };
      }
      if (sales.classList.contains('sm-sort-selected')) {
        return { success: true, clicked: false, mode: sortMode, alreadyActive: true };
      }
      sales.click();
      return { success: true, clicked: true, mode: sortMode, alreadyActive: false };
    })()`,
    returnByValue: true,
  });
  const sorting = result?.result?.value;
  if (!sorting?.success) {
    throw new Error(sorting?.error || '切换商品排序失败。');
  }
  return sorting;
}

function formatPrice(value) {
  return Number(value).toFixed(2);
}

async function scrollToBottomForProducts(tabId) {
  const startedAt = Date.now();
  let maxObservedCount = 0;

  while (Date.now() - startedAt < LIMITS.productRenderTimeoutMs) {
    const snapshot = await readProductListSnapshot(tabId);
    maxObservedCount = Math.max(maxObservedCount, Number(snapshot.count ?? 0));
    if (hasReachedPageBottom(snapshot)) {
      return {
        selector: SELECTORS.productCard,
        reachedBottom: true,
        countAtBottom: Number(snapshot.count ?? 0),
        maxObservedCount,
        wheelStepPx: LIMITS.wheelDownStepPx,
        wheelStepDelayMs: LIMITS.wheelStepDelayMs,
      };
    }

    await dispatchMouseWheel(tabId, snapshot, LIMITS.wheelDownStepPx);
    await delay(LIMITS.wheelStepDelayMs);
  }
  throw new Error('模拟鼠标滚轮向下滚动到1688页面底部超时。');
}

async function readProductListSnapshot(tabId) {
  const result = await sendDebuggerCommand(tabId, 'Runtime.evaluate', {
    expression: `(() => ({
      count: document.querySelectorAll(${JSON.stringify(SELECTORS.productCard)}).length,
      scrollHeight: Math.max(
        document.documentElement?.scrollHeight ?? 0,
        document.body?.scrollHeight ?? 0
      ),
      scrollY: window.scrollY,
      viewportWidth: window.innerWidth,
      viewportHeight: window.innerHeight
    }))()`,
    returnByValue: true,
  });
  return result?.result?.value ?? {};
}

async function dispatchMouseWheel(tabId, snapshot, deltaY) {
  const viewportWidth = Math.max(1, Number(snapshot.viewportWidth ?? 1));
  const viewportHeight = Math.max(1, Number(snapshot.viewportHeight ?? 1));
  await sendDebuggerCommand(tabId, 'Input.dispatchMouseEvent', {
    type: 'mouseWheel',
    x: Math.floor(viewportWidth / 2),
    y: Math.floor(viewportHeight / 2),
    deltaX: 0,
    deltaY,
    pointerType: 'mouse',
  });
}

function hasReachedPageBottom(snapshot) {
  return (
    Number(snapshot.scrollY ?? 0) + Number(snapshot.viewportHeight ?? 0) >=
    Number(snapshot.scrollHeight ?? 0) - 4
  );
}

async function jumpToPageTop(tabId) {
  const result = await sendDebuggerCommand(tabId, 'Runtime.evaluate', {
    expression: `(() => {
      window.scrollTo(0, 0);
      return window.scrollY;
    })()`,
    returnByValue: true,
  });
  if (Number(result?.result?.value ?? 0) > 4) {
    throw new Error('商品数据已获取，但页面未能跳转到顶部。');
  }
}

async function resolveDynamicDetailUrls(tabId, extractionData, job) {
  const allUnresolved = Array.isArray(extractionData?.diagnostics?.unresolvedDetailCards)
    ? extractionData.diagnostics.unresolvedDetailCards
    : [];
  const unresolved = allUnresolved.slice(0, LIMITS.maxDynamicDetailUrlCards);
  const report = {
    requestedCount: unresolved.length,
    unattemptedCount: Math.max(0, allUnresolved.length - unresolved.length),
    resolvedCount: 0,
    failedCount: 0,
    remainingCount: allUnresolved.length,
    items: [],
  };
  if (!unresolved.length) {
    extractionData.diagnostics.finalValidDetailUrlCount = extractionData.items.length;
    report.remainingCount = allUnresolved.length;
    return report;
  }

  await updateState({
    status: 'running',
    jobId: job.jobId,
    step: `正在解析 ${unresolved.length} 条广告商品的真实详情地址`,
  });
  for (const entry of unresolved) {
    const item = extractionData.items?.[entry.itemIndex];
    if (!item) {
      report.failedCount += 1;
      report.items.push({ ...entry, success: false, error: '商品索引无效。' });
      continue;
    }

    try {
      const detailUrl = await clickCardAndResolveDetailUrl(tabId, entry.cardIndex);
      item.detailUrl = detailUrl;
      report.resolvedCount += 1;
      report.items.push({ ...entry, success: true, detailUrl });
    } catch (error) {
      report.failedCount += 1;
      report.items.push({ ...entry, success: false, error: error.message });
    }
  }

  extractionData.diagnostics.dynamicResolvedDetailUrlCount = report.resolvedCount;
  extractionData.diagnostics.finalValidDetailUrlCount = extractionData.items.filter(
    (item) => isProductDetailUrl(item?.detailUrl),
  ).length;
  report.remainingCount = Math.max(
    0,
    extractionData.items.length - extractionData.diagnostics.finalValidDetailUrlCount,
  );
  return report;
}

async function clickCardAndResolveDetailUrl(sourceTabId, cardIndex) {
  const position = await sendDebuggerCommand(sourceTabId, 'Runtime.evaluate', {
    expression: `(() => {
      const card = document.querySelectorAll(${JSON.stringify(SELECTORS.productCard)})[${Number(cardIndex)}];
      const target = card?.querySelector('.ad-offer-img-wrapper, .offer-title-row');
      if (!target) return null;
      target.scrollIntoView({ behavior: 'instant', block: 'center', inline: 'nearest' });
      const rect = target.getBoundingClientRect();
      return {
        x: Math.floor(rect.left + rect.width / 2),
        y: Math.floor(rect.top + rect.height / 2),
        width: rect.width,
        height: rect.height
      };
    })()`,
    returnByValue: true,
  });
  const point = position?.result?.value;
  if (!point || point.width <= 0 || point.height <= 0) {
    throw new Error('无法定位广告商品卡片的可点击区域。');
  }

  const detailTabWaiter = createProductDetailTabWaiter(
    sourceTabId,
    LIMITS.dynamicDetailUrlTimeoutMs,
  );
  try {
    await delay(LIMITS.dynamicDetailClickDelayMs);
    await sendDebuggerCommand(sourceTabId, 'Input.dispatchMouseEvent', {
      type: 'mousePressed',
      x: point.x,
      y: point.y,
      button: 'left',
      clickCount: 1,
    });
    await sendDebuggerCommand(sourceTabId, 'Input.dispatchMouseEvent', {
      type: 'mouseReleased',
      x: point.x,
      y: point.y,
      button: 'left',
      clickCount: 1,
    });
  } catch (error) {
    detailTabWaiter.cancel();
    await detailTabWaiter.promise.catch(() => {});
    throw error;
  }

  const detailTab = await detailTabWaiter.promise;
  assertTabId(detailTab.id);
  try {
    const detailUrl = normalizeProductDetailUrl(detailTab.url);
    if (!detailUrl) throw new Error('广告卡片没有打开有效的1688商品详情页。');
    return detailUrl;
  } finally {
    try {
      await chrome.tabs.remove(detailTab.id);
    } catch {
      // 商品详情标签页可能已被用户关闭。
    }
    try {
      await chrome.tabs.update(sourceTabId, { active: true });
    } catch {
      // 搜索标签页可能已被用户关闭。
    }
  }
}

function createProductDetailTabWaiter(sourceTabId, timeoutMs) {
  let cancel = () => {};
  const promise = new Promise((resolve, reject) => {
    const candidateTabIds = new Set();
    let settled = false;
    const finish = (error, tab) => {
      if (settled) return;
      settled = true;
      clearTimeout(timeoutId);
      chrome.tabs.onCreated.removeListener(onCreated);
      chrome.tabs.onUpdated.removeListener(onUpdated);
      chrome.tabs.onRemoved.removeListener(onRemoved);
      if (error) reject(error);
      else resolve(tab);
    };
    const inspect = (tab) => {
      if (!tab?.id || !candidateTabIds.has(tab.id)) return;
      if (normalizeProductDetailUrl(tab.url)) finish(null, tab);
    };
    const onCreated = (tab) => {
      if (tab.openerTabId !== sourceTabId) return;
      candidateTabIds.add(tab.id);
      inspect(tab);
    };
    const onUpdated = (tabId, changeInfo, tab) => {
      if (!candidateTabIds.has(tabId)) return;
      if (changeInfo.url || changeInfo.status === 'complete') inspect(tab);
    };
    const onRemoved = (tabId) => {
      if (candidateTabIds.has(tabId)) candidateTabIds.delete(tabId);
    };
    const timeoutId = setTimeout(
      () => finish(new Error('等待广告商品详情页打开超时。')),
      timeoutMs,
    );
    chrome.tabs.onCreated.addListener(onCreated);
    chrome.tabs.onUpdated.addListener(onUpdated);
    chrome.tabs.onRemoved.addListener(onRemoved);
    cancel = () => finish(new Error('广告商品详情页等待已取消。'));
  });
  return { promise, cancel };
}

function normalizeProductDetailUrl(value) {
  try {
    const url = new URL(value);
    if (url.protocol === 'http:') url.protocol = 'https:';
    return isProductDetailUrl(url.href) ? url.href : null;
  } catch {
    return null;
  }
}

function isProductDetailUrl(value) {
  if (!isAllowed1688Url(value)) return false;
  try {
    const url = new URL(value);
    return (
      ['detail.1688.com', 'detail.m.1688.com', 'm.1688.com'].includes(
        url.hostname.toLowerCase(),
      ) &&
      (/\/offer\/\d+(?:\.html)?(?:\/|$)/i.test(url.pathname) ||
        /^\d+$/.test(url.searchParams.get('offerId') ?? ''))
    );
  } catch {
    return false;
  }
}

function formatCompletionStep(data) {
  const rawCardCount = Number(data.diagnostics?.extraction?.rawCardCount ?? 0);
  const filteredCount = Math.max(0, rawCardCount - Number(data.count ?? 0));
  const listStep = filteredCount
    ? `已获取 ${data.count} 条商品；DOM共 ${rawCardCount} 条，另有 ${filteredCount} 条未输出`
    : `已获取 ${data.count} 条商品`;
  const unresolvedDetailUrlCount = Number(
    data.diagnostics?.detailUrlResolution?.remainingCount ?? 0,
  );
  const linkStep = unresolvedDetailUrlCount > 0
    ? `${listStep}；${unresolvedDetailUrlCount} 条广告商品详情地址尚未解析`
    : listStep;
  if (!Object.hasOwn(data, 'detailDom')) return linkStep;
  if (data.detailDom?.success) {
    const exportStep = data.detailDom.download?.state === 'complete'
      ? '已导出'
      : '已生成并开始导出';
    return `${linkStep}；第2条详情DOM${exportStep}`;
  }
  return `${linkStep}；第2条详情DOM获取失败，详见结果`;
}

async function captureDefaultRenderedDetail(job, items) {
  const itemIndex = DETAIL_DOM_OPTIONS.defaultItemIndex;
  const item = Array.isArray(items) ? items[itemIndex] : null;
  const base = {
    itemIndex,
    itemPosition: itemIndex + 1,
    productTitle: item?.title ?? null,
    detailUrl: item?.detailUrl ?? null,
  };
  if (!item?.detailUrl) {
    return { ...base, success: false, error: '列表没有可用的第2条商品详情链接。' };
  }
  if (!isAllowed1688Url(item.detailUrl)) {
    return { ...base, success: false, error: '第2条商品详情链接不属于1688。' };
  }

  let tabId;
  try {
    const detailTab = await chrome.tabs.create({
      url: item.detailUrl,
      active: false,
    });
    assertTabId(detailTab.id);
    tabId = detailTab.id;
    job.temporaryTabIds ??= new Set();
    job.temporaryTabIds.add(tabId);

    await waitForTabComplete(tabId, DETAIL_DOM_OPTIONS.renderedPageTimeoutMs);
    const loadedTab = await chrome.tabs.get(tabId);
    if (!isAllowed1688Url(loadedTab.url)) {
      throw new Error(`详情页跳转到了不受信任的地址：${loadedTab.url || '未知地址'}`);
    }

    await chrome.scripting.executeScript({
      target: { tabId },
      files: ['content/detail-extractor.js'],
    });
    const extraction = await waitForRenderedDetailExtraction(
      tabId,
      DETAIL_DOM_OPTIONS.renderedPageTimeoutMs,
    );
    if (!isAllowed1688Url(extraction.finalUrl)) {
      throw new Error('详情采集结果中的最终地址不属于1688。');
    }

    return {
      ...base,
      success: true,
      mode: 'rendered-chrome-tab',
      requestedUrl: item.detailUrl,
      finalUrl: extraction.finalUrl,
      capturedAt: extraction.capturedAt,
      pageTitle: extraction.pageTitle,
      facts: extraction.facts ?? [],
      raw: extraction.raw ?? null,
      factDiagnostics: extraction.diagnostics ?? null,
    };
  } catch (error) {
    return { ...base, success: false, error: error.message };
  } finally {
    if (Number.isInteger(tabId)) {
      job.temporaryTabIds?.delete(tabId);
      try {
        await chrome.tabs.remove(tabId);
      } catch {
        // 用户可能在采集完成前主动关闭了临时详情标签页。
      }
    }
  }
}

async function waitForRenderedDetailExtraction(tabId, timeoutMs) {
  const startedAt = Date.now();
  let attempts = 0;
  let latestDiagnostics = null;
  let latestError = null;

  while (Date.now() - startedAt < timeoutMs) {
    attempts += 1;
    try {
      const response = await chrome.tabs.sendMessage(tabId, {
        type: 'EXTRACT_1688_RENDERED_DETAIL',
        options: {
          maxFacts: DETAIL_DOM_OPTIONS.maxFacts,
          maxImages: DETAIL_DOM_OPTIONS.maxImages,
          maxPriceTexts: DETAIL_DOM_OPTIONS.maxPriceTexts,
          maxSkuTexts: DETAIL_DOM_OPTIONS.maxSkuTexts,
        },
      });
      if (!response?.success) {
        latestError = response?.error || '详情页解析器没有返回有效结果。';
      } else {
        latestDiagnostics = response.data?.diagnostics ?? null;
        if (latestDiagnostics?.blocked) {
          throw new Error('1688详情页触发了验证码或访问限制。');
        }
        if (response.data?.ready) {
          return {
            ...response.data,
            diagnostics: {
              ...latestDiagnostics,
              attempts,
              waitMs: Date.now() - startedAt,
            },
          };
        }
      }
    } catch (error) {
      if (/验证码|访问限制/.test(error.message)) throw error;
      latestError = error.message;
    }
    await delay(DETAIL_DOM_OPTIONS.renderedPollDelayMs);
  }

  const diagnostics = latestDiagnostics
    ? `；最后诊断：${JSON.stringify(latestDiagnostics)}`
    : '';
  const reason = latestError ? `；最后错误：${latestError}` : '';
  throw new Error(`等待1688详情属性渲染超时${diagnostics}${reason}`);
}

async function captureDefaultDetailDom(items, exportRawDom = true) {
  const itemIndex = DETAIL_DOM_OPTIONS.defaultItemIndex;
  const item = Array.isArray(items) ? items[itemIndex] : null;
  const base = {
    itemIndex,
    itemPosition: itemIndex + 1,
    productTitle: item?.title ?? null,
    detailUrl: item?.detailUrl ?? null,
  };
  if (!item?.detailUrl) {
    return { ...base, success: false, error: '列表没有可用的第2条商品详情链接。' };
  }
  if (!isAllowed1688Url(item.detailUrl)) {
    return { ...base, success: false, error: '第2条商品详情链接不属于1688。' };
  }

  try {
    await ensureOffscreenDocument();
    const response = await chrome.runtime.sendMessage({
      target: 'offscreen',
      type: 'PARSE_1688_DETAIL_DOM',
      detailUrl: item.detailUrl,
      options: {
        ...DETAIL_DOM_OPTIONS,
        createDownload: exportRawDom,
        includeStructure: exportRawDom,
      },
    });
    if (!response?.success) {
      throw new Error(response?.error || '隐藏详情DOM解析失败。');
    }

    const { downloadUrl, structure, ...metadata } = response.data;
    if (!exportRawDom) {
      await closeOffscreenDocument();
      return {
        ...base,
        success: true,
        mode: 'fetched-html',
        ...metadata,
      };
    }
    const filename = createDetailDomFilename(metadata.finalUrl, itemIndex);
    const downloadId = await chrome.downloads.download({
      url: downloadUrl,
      filename,
      conflictAction: 'uniquify',
      saveAs: false,
    });
    const downloadState = await waitForDownloadTerminal(
      downloadId,
      DETAIL_DOM_OPTIONS.downloadTimeoutMs,
    );
    if (downloadState !== 'in_progress') {
      await revokeDetailDomDownloadUrl(downloadUrl);
      await closeOffscreenDocument();
    }

    return {
      ...base,
      success: true,
      mode: 'fetched-html',
      ...metadata,
      structure,
      download: {
        id: downloadId,
        filename,
        state: downloadState,
      },
    };
  } catch (error) {
    await closeOffscreenDocument();
    return { ...base, success: false, error: error.message };
  }
}

async function ensureOffscreenDocument() {
  const documentUrl = chrome.runtime.getURL('offscreen.html');
  const contexts = await chrome.runtime.getContexts({
    contextTypes: ['OFFSCREEN_DOCUMENT'],
    documentUrls: [documentUrl],
  });
  if (contexts.length) return;

  if (!creatingOffscreenDocument) {
    creatingOffscreenDocument = chrome.offscreen.createDocument({
      url: 'offscreen.html',
      reasons: ['DOM_PARSER'],
      justification: 'Parse one fetched 1688 detail page without opening a visible tab.',
    });
  }
  try {
    await creatingOffscreenDocument;
  } finally {
    creatingOffscreenDocument = null;
  }
}

async function closeOffscreenDocument() {
  try {
    const contexts = await chrome.runtime.getContexts({
      contextTypes: ['OFFSCREEN_DOCUMENT'],
    });
    if (contexts.length) await chrome.offscreen.closeDocument();
  } catch {
    // 离屏文档可能已被Chrome回收。
  }
}

async function revokeDetailDomDownloadUrl(url) {
  try {
    await chrome.runtime.sendMessage({
      target: 'offscreen',
      type: 'REVOKE_DETAIL_DOM_DOWNLOAD_URL',
      url,
    });
  } catch {
    // 关闭离屏文档也会释放Blob URL。
  }
}

function waitForDownloadTerminal(downloadId, timeoutMs) {
  return new Promise((resolve) => {
    let settled = false;
    const finish = (state) => {
      if (settled) return;
      settled = true;
      clearTimeout(timeoutId);
      chrome.downloads.onChanged.removeListener(onChanged);
      resolve(state);
    };
    const onChanged = (delta) => {
      if (delta.id !== downloadId || !delta.state?.current) return;
      if (['complete', 'interrupted'].includes(delta.state.current)) {
        finish(delta.state.current);
      }
    };
    const timeoutId = setTimeout(() => finish('in_progress'), timeoutMs);
    chrome.downloads.onChanged.addListener(onChanged);
    void chrome.downloads.search({ id: downloadId }).then(([download]) => {
      if (download?.state && download.state !== 'in_progress') finish(download.state);
    });
  });
}

function createDetailDomFilename(detailUrl, itemIndex) {
  const offerId = extractOfferId(detailUrl);
  const fallback = new Date().toISOString().replace(/[:.]/g, '-');
  const identifier = offerId || fallback;
  return `auto-magic/detail-dom/item-${String(itemIndex + 1).padStart(3, '0')}-${identifier}.dom.html.txt`;
}

function extractOfferId(value) {
  try {
    const url = new URL(value);
    const queryOfferId = url.searchParams.get('offerId');
    if (/^\d+$/.test(queryOfferId ?? '')) return queryOfferId;
    return url.pathname.match(/\/offer\/(\d+)/)?.[1] ?? null;
  } catch {
    return null;
  }
}

async function showLatestDetailDomDownload() {
  const { demoState = {} } = await chrome.storage.local.get(['demoState']);
  const downloadId = demoState.result?.data?.detailDom?.download?.id;
  if (!Number.isInteger(downloadId)) throw new Error('没有可显示的详情DOM导出文件。');
  chrome.downloads.show(downloadId);
}

function waitForSearchResultTab(sourceTabId, timeoutMs) {
  return new Promise((resolve, reject) => {
    const candidateTabIds = new Set([sourceTabId]);
    let settled = false;

    const finish = (error, tab) => {
      if (settled) return;
      settled = true;
      clearTimeout(timeoutId);
      chrome.tabs.onCreated.removeListener(onCreated);
      chrome.tabs.onUpdated.removeListener(onUpdated);
      if (error) reject(error);
      else resolve(tab);
    };
    const inspectTab = (tab) => {
      if (!tab?.id || !candidateTabIds.has(tab.id)) return;
      if (!tab.url || !isAllowed1688Url(tab.url)) return;
      if (isSearchResultUrl(tab.url)) finish(null, tab);
    };
    const onCreated = (tab) => {
      if (tab.openerTabId !== sourceTabId) return;
      candidateTabIds.add(tab.id);
      inspectTab(tab);
    };
    const onUpdated = (tabId, changeInfo, tab) => {
      if (!candidateTabIds.has(tabId)) return;
      if (changeInfo.url || changeInfo.status === 'complete') inspectTab(tab);
    };
    const timeoutId = setTimeout(
      () => finish(new Error('没有识别到1688搜索结果标签页。')),
      timeoutMs,
    );

    chrome.tabs.onCreated.addListener(onCreated);
    chrome.tabs.onUpdated.addListener(onUpdated);
  });
}

async function waitForTabComplete(tabId, timeoutMs) {
  const current = await chrome.tabs.get(tabId);
  if (current.status === 'complete') return current;

  return new Promise((resolve, reject) => {
    const finish = (error, tab) => {
      clearTimeout(timeoutId);
      chrome.tabs.onUpdated.removeListener(onUpdated);
      chrome.tabs.onRemoved.removeListener(onRemoved);
      if (error) reject(error);
      else resolve(tab);
    };
    const onUpdated = (updatedTabId, changeInfo, tab) => {
      if (updatedTabId === tabId && changeInfo.status === 'complete') finish(null, tab);
    };
    const onRemoved = (removedTabId) => {
      if (removedTabId === tabId) finish(new Error('任务标签页已被关闭。'));
    };
    const timeoutId = setTimeout(
      () => finish(new Error('等待页面加载超时。')),
      timeoutMs,
    );
    chrome.tabs.onUpdated.addListener(onUpdated);
    chrome.tabs.onRemoved.addListener(onRemoved);
  });
}

async function updateState(patch) {
  const { demoState = {} } = await chrome.storage.local.get(['demoState']);
  await chrome.storage.local.set({ demoState: { ...demoState, ...patch } });
}

async function setBadge(text, color) {
  await chrome.action.setBadgeBackgroundColor({ color });
  await chrome.action.setBadgeText({ text });
}

function assertTabId(tabId) {
  if (!Number.isInteger(tabId)) throw new Error('Chrome没有返回有效的标签页ID。');
}

function delay(ms) {
  return new Promise((resolve) => setTimeout(resolve, ms));
}

async function ensureNativeReconnectAlarm() {
  const existing = await chrome.alarms.get(NATIVE_RECONNECT_ALARM);
  if (!existing) {
    await chrome.alarms.create(NATIVE_RECONNECT_ALARM, { periodInMinutes: 0.5 });
  }
}

function connectNativeHost() {
  if (nativePort) return;
  if (nativeReconnectTimer) {
    clearTimeout(nativeReconnectTimer);
    nativeReconnectTimer = null;
  }

  try {
    const port = chrome.runtime.connectNative(NATIVE_HOST_NAME);
    nativePort = port;
    port.onMessage.addListener(handleNativeMessage);
    port.onDisconnect.addListener(() => {
      void chrome.runtime.lastError;
      if (nativePort === port) nativePort = null;
      scheduleNativeReconnect();
    });
    postNativeMessage(createNativeEnvelope(
      NATIVE_MESSAGE_TYPES.extensionReady,
      crypto.randomUUID(),
      { extensionVersion: chrome.runtime.getManifest().version },
    ));
  } catch (error) {
    console.warn('连接 Auto Magic 桌面桥接失败。', error);
    nativePort = null;
    scheduleNativeReconnect();
  }
}

function scheduleNativeReconnect() {
  if (nativeReconnectTimer) return;
  nativeReconnectTimer = setTimeout(() => {
    nativeReconnectTimer = null;
    connectNativeHost();
  }, NATIVE_RECONNECT_DELAY_MS);
}

function postNativeMessage(message) {
  if (!nativePort) return false;
  try {
    nativePort.postMessage(message);
    return true;
  } catch {
    nativePort = null;
    scheduleNativeReconnect();
    return false;
  }
}

function handleNativeMessage(message) {
  if (message?.type === NATIVE_MESSAGE_TYPES.bridgeStatus) {
    if (message.error?.message) console.warn(message.error.message);
    return;
  }

  const fallbackRequestId = String(message?.requestId ?? crypto.randomUUID());
  try {
    const desktopRequest = normalizeDesktopSearchEnvelope(message);
    if (activeJob) {
      postNativeMessage(createNativeEnvelope(
        NATIVE_MESSAGE_TYPES.searchFailed,
        desktopRequest.requestId,
        undefined,
        { code: 'SEARCH_ALREADY_RUNNING', message: '已有采集任务正在执行。' },
      ));
      return;
    }

    const request = normalizeStartRequest({
      type: 'START_1688_DOM_DEMO',
      keyword: desktopRequest.keyword,
      maxItems: desktopRequest.maxItems,
      includeDetailDom: false,
      procurementMinimumCny: desktopRequest.procurementMinimumCny,
      procurementMaximumCny: desktopRequest.procurementMaximumCny,
      sortMode: desktopRequest.sortMode,
      includeDetailFacts: desktopRequest.includeDetailFacts,
    });
    const jobId = crypto.randomUUID();
    const job = {
      jobId,
      desktopRequestId: desktopRequest.requestId,
      createdTabIds: new Set(),
      attachedTabIds: new Set(),
      temporaryTabIds: new Set(),
    };
    activeJob = job;
    postNativeMessage(createNativeEnvelope(
      NATIVE_MESSAGE_TYPES.searchAccepted,
      desktopRequest.requestId,
      { jobId },
    ));
    void executeSearchJob(job, request);
  } catch (error) {
    postNativeMessage(createNativeEnvelope(
      NATIVE_MESSAGE_TYPES.searchFailed,
      fallbackRequestId,
      undefined,
      { code: 'INVALID_SEARCH_REQUEST', message: error.message },
    ));
  }
}
