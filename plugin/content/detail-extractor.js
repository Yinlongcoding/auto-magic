(() => {
  if (globalThis.__AUTO_MAGIC_1688_DETAIL_EXTRACTOR__) return;
  globalThis.__AUTO_MAGIC_1688_DETAIL_EXTRACTOR__ = true;

  const MAX_LABEL_LENGTH = 120;
  const MAX_VALUE_LENGTH = 2_000;
  const DEFAULT_MAX_FACTS = 500;
  const DEFAULT_MAX_IMAGES = 100;
  const DEFAULT_MAX_PRICE_TEXTS = 30;
  const DEFAULT_MAX_SKU_TEXTS = 100;

  chrome.runtime.onMessage.addListener((message, _sender, sendResponse) => {
    if (message?.type === 'PREPARE_1688_RENDERED_DETAIL') {
      void prepareLazyContent(message.options).then(
        (data) => sendResponse({ success: true, data }),
        (error) => sendResponse({ success: false, error: error.message }),
      );
      return true;
    }

    if (message?.type !== 'EXTRACT_1688_RENDERED_DETAIL') return false;

    void extractRenderedDetail(message.options).then(
      (data) => sendResponse({ success: true, data }),
      (error) => sendResponse({ success: false, error: error.message }),
    );
    return true;
  });

  async function extractRenderedDetail(options = {}) {
    const maxFacts = normalizeLimit(options.maxFacts, 1, 2_000, DEFAULT_MAX_FACTS);
    const maxImages = normalizeLimit(options.maxImages, 1, 300, DEFAULT_MAX_IMAGES);
    const maxPriceTexts = normalizeLimit(
      options.maxPriceTexts,
      1,
      100,
      DEFAULT_MAX_PRICE_TEXTS,
    );
    const maxSkuTexts = normalizeLimit(
      options.maxSkuTexts,
      1,
      500,
      DEFAULT_MAX_SKU_TEXTS,
    );
    const candidates = [];

    collectDecisionAttributes(candidates);
    collectNormalAttributes(candidates);
    collectCpvAttributes(candidates);
    if (!candidates.length) collectSemanticPairs(candidates);

    const attributes = normalizeCandidates(candidates, maxFacts);
    const colorOptions = collectNormalizedColorOptions(attributes);
    const skuCapture = await collectStructuredSkuCapture(attributes, colorOptions, options);
    const title = normalizeProductTitle(document.title);
    const facts = normalizeCandidates([
      ...(title ? [{ label: '商品名称', value: title, source: 'document.title' }] : []),
      ...attributes,
    ], maxFacts);
    const imageCapture = collectAllImageUrls(maxImages);
    const imageUrls = imageCapture.urls;
    const priceTexts = collectVisibleTexts(
      '[class*="price"], [class*="Price"]',
      /(?:[¥￥]\s*\d|\d(?:[\d,.]*\d)?\s*元)/,
      maxPriceTexts,
    );
    const skuTexts = collectVisibleTexts(
      '[class*="sku"] [class*="item"], [class*="sku"] [class*="value"], [class*="spec"] [class*="item"]',
      /\S/,
      maxSkuTexts,
    );
    const pageTextSample = cleanText(document.body?.innerText).slice(0, 1_500);
    const blocked = /验证码|安全验证|访问受限|滑块验证|punish/i.test(
      `${document.title} ${location.href} ${pageTextSample}`,
    );
    const sourceCounts = countSources(attributes);

    const lazyLoad = globalThis.__AUTO_MAGIC_DETAIL_LAZY_STATE__ ?? null;
    return {
      ready: !blocked && lazyLoad?.completed === true,
      finalUrl: location.href,
      capturedAt: new Date().toISOString(),
      pageTitle: document.title || null,
      facts,
      raw: {
        offerId: extractOfferId(location.href),
        title,
        attributes,
        colorOptions,
        skuDimensions: skuCapture.dimensions,
        skuCombinations: skuCapture.combinations,
        skuMatrixStatus: skuCapture.status,
        imageUrls,
        priceTexts,
        skuTexts,
      },
      diagnostics: {
        mode: 'rendered-chrome-tab',
        blocked,
        candidateCount: candidates.length,
        attributeCount: attributes.length,
        factCount: facts.length,
        imageCount: imageUrls.length,
        imageCapture: imageCapture.diagnostics,
        priceTextCount: priceTexts.length,
        skuTextCount: skuTexts.length,
        skuMatrix: skuCapture.diagnostics,
        sources: sourceCounts,
        selectors: {
          decisionAttributes: document.querySelectorAll('.decision-attributes-list > li').length,
          normalAttributes: document.querySelectorAll('.normal-attributes-table tbody tr').length,
          cpvAttributes: document.querySelectorAll('.cpv-attr-item').length,
        },
        lazyLoad,
      },
    };
  }

  async function prepareLazyContent(options = {}) {
    const maxWaitMs = normalizeLimit(options.maxWaitMs, 1_000, 60_000, 12_000);
    const stepDelayMs = normalizeLimit(options.stepDelayMs, 50, 2_000, 180);
    const bottomSettleMs = normalizeLimit(options.bottomSettleMs, 100, 3_000, 600);
    const returnTopSettleMs = normalizeLimit(options.returnTopSettleMs, 50, 2_000, 200);
    const startedAt = Date.now();
    const initialHeight = getDocumentHeight();
    let scrollSteps = 0;
    let bottomChecks = 0;
    let reachedBottom = false;

    if (isBlockedPage()) throw new Error('1688详情页触发了验证码或访问限制。');
    window.scrollTo({ top: 0, behavior: 'instant' });
    while (Date.now() - startedAt < maxWaitMs) {
      if (isBlockedPage()) throw new Error('1688详情页触发了验证码或访问限制。');
      const height = getDocumentHeight();
      const viewportHeight = Math.max(window.innerHeight, 600);
      const nextTop = Math.min(window.scrollY + Math.floor(viewportHeight * 0.85), height);
      window.scrollTo({ top: nextTop, behavior: 'instant' });
      scrollSteps += 1;
      await delay(stepDelayMs);
      const currentHeight = getDocumentHeight();
      reachedBottom = window.scrollY + window.innerHeight >= currentHeight - 8;
      if (!reachedBottom) continue;

      bottomChecks += 1;
      await delay(bottomSettleMs);
      const settledHeight = getDocumentHeight();
      const stillAtBottom = window.scrollY + window.innerHeight >= settledHeight - 8;
      if (stillAtBottom && settledHeight === currentHeight) break;
    }

    window.scrollTo({ top: 0, behavior: 'instant' });
    await delay(returnTopSettleMs);
    const state = {
      completed: true,
      reachedBottom,
      timedOut: !reachedBottom,
      elapsedMs: Date.now() - startedAt,
      scrollSteps,
      bottomChecks,
      initialHeight,
      finalHeight: getDocumentHeight(),
      signature: readLazySignature(),
    };
    globalThis.__AUTO_MAGIC_DETAIL_LAZY_STATE__ = state;
    return state;
  }

  function isBlockedPage() {
    const sample = cleanText(document.body?.innerText).slice(0, 1_500);
    return /验证码|安全验证|访问受限|滑块验证|punish/i.test(
      `${document.title} ${location.href} ${sample}`,
    );
  }

  function getDocumentHeight() {
    return Math.max(
      document.documentElement?.scrollHeight ?? 0,
      document.body?.scrollHeight ?? 0,
    );
  }

  function readLazySignature() {
    return [
      document.querySelectorAll('img').length,
      document.querySelectorAll('[data-src], [data-lazy-src], [data-original]').length,
      document.querySelectorAll('.normal-attributes-table tbody tr').length,
      document.querySelectorAll('.cpv-attr-item').length,
      Math.floor((document.body?.innerText?.length ?? 0) / 100),
    ].join(':');
  }

  function collectDecisionAttributes(candidates) {
    for (const item of document.querySelectorAll('.decision-attributes-list > li')) {
      const children = Array.from(item.children).map((child) => cleanText(child.textContent));
      if (children.length >= 2) {
        candidates.push({
          label: children[0],
          value: children.slice(1).join(' '),
          source: 'decision-attributes',
        });
      }
    }
  }

  function collectNormalAttributes(candidates) {
    for (const row of document.querySelectorAll('.normal-attributes-table tbody tr')) {
      collectTableRowPairs(row, candidates, 'normal-attributes');
    }
  }

  function collectCpvAttributes(candidates) {
    for (const item of document.querySelectorAll('.cpv-attr-item')) {
      candidates.push({
        label: item.querySelector('.cpv-attr-label')?.textContent,
        value: item.querySelector('.cpv-attr-value')?.textContent,
        source: 'cpv-attributes',
      });
    }
  }

  function collectSemanticPairs(candidates) {
    for (const list of document.querySelectorAll('dl')) {
      const terms = Array.from(list.querySelectorAll(':scope > dt'));
      for (const term of terms) {
        const definition = term.nextElementSibling;
        if (definition?.matches('dd')) {
          candidates.push({
            label: term.textContent,
            value: definition.textContent,
            source: 'semantic-dl',
          });
        }
      }
    }

    for (const row of document.querySelectorAll('table tr')) {
      if (row.closest('.normal-attributes-table')) continue;
      collectTableRowPairs(row, candidates, 'semantic-table');
    }
  }

  function collectTableRowPairs(row, candidates, source) {
    const cells = Array.from(row.querySelectorAll(':scope > th, :scope > td'));
    if (cells.length < 2) return;

    if (cells.length % 2 === 0) {
      for (let index = 0; index < cells.length; index += 2) {
        candidates.push({
          label: cells[index].textContent,
          value: cells[index + 1].textContent,
          source,
        });
      }
      return;
    }

    candidates.push({
      label: cells[0].textContent,
      value: cells[cells.length - 1].textContent,
      source,
    });
  }

  function normalizeCandidates(candidates, limit) {
    const result = [];
    const seen = new Set();
    for (const candidate of candidates) {
      const label = cleanText(candidate?.label);
      const value = cleanText(candidate?.value);
      if (!isUsablePair(label, value)) continue;
      const identity = `${normalizeIdentity(label)}\u0000${normalizeIdentity(value)}`;
      if (seen.has(identity)) continue;
      seen.add(identity);
      result.push({
        label: truncate(label, MAX_LABEL_LENGTH),
        value: truncate(value, MAX_VALUE_LENGTH),
        source: cleanText(candidate?.source) || 'rendered-dom',
      });
      if (result.length >= limit) break;
    }
    return result;
  }

  function collectAllImageUrls(limit) {
    const selector = '.od-picture-gallery-list > .v-image-cover';
    const galleryItems = Array.from(document.querySelectorAll(selector));
    const productItems = galleryItems.length > 0
      ? galleryItems.slice(0, -1)
      : [];
    const urls = new Set();
    let invalidBackgroundImageCount = 0;
    let duplicateCount = 0;
    for (const item of productItems) {
      const rawUrl = readInlineBackgroundImageUrl(item);
      const normalized = normalizeProductImageUrl(rawUrl);
      if (!normalized) {
        invalidBackgroundImageCount += 1;
        continue;
      }
      if (urls.has(normalized)) duplicateCount += 1;
      urls.add(normalized);
      if (urls.size >= limit) break;
    }

    const detailSelector = [
      '#detailContent img',
      '.detail-content img',
      '.desc-lazyload-container img',
      '[class*="description"] img',
      '[class*="detail"] img',
      '[class*="description"] source',
      '[class*="detail"] source',
    ].join(', ');
    const detailItems = Array.from(document.querySelectorAll(detailSelector));
    let detailCandidateCount = 0;
    for (const item of detailItems) {
      const candidates = readElementImageUrls(item);
      detailCandidateCount += candidates.length;
      for (const candidate of candidates) {
        const normalized = normalizeProductImageUrl(candidate);
        if (!normalized) continue;
        if (urls.has(normalized)) duplicateCount += 1;
        urls.add(normalized);
        if (urls.size >= limit) break;
      }
      if (urls.size >= limit) break;
    }

    return {
      urls: [...urls],
      diagnostics: {
        selector: `${selector}, ${detailSelector}`,
        source: 'gallery-and-lazy-detail-images',
        candidateCount: galleryItems.length,
        detailElementCount: detailItems.length,
        detailCandidateCount,
        excludedLastItemCount: galleryItems.length > 0 ? 1 : 0,
        inspectedCount: productItems.length,
        invalidBackgroundImageCount,
        duplicateCount,
        outputCount: urls.size,
      },
    };
  }

  function collectNormalizedColorOptions(attributes) {
    const normalizeColorOptions = globalThis.__AUTO_MAGIC_DETAIL_NORMALIZERS__?.normalizeColorOptions;
    if (typeof normalizeColorOptions !== 'function') return [];
    return attributes
      .filter((attribute) => /^(?:颜色|颜色分类|色彩)$/u.test(cleanText(attribute.label)))
      .flatMap((attribute) => normalizeColorOptions(attribute.value));
  }

  async function collectStructuredSkuCapture(attributes, colorOptions, options) {
    const normalizeSimpleOptions = globalThis.__AUTO_MAGIC_DETAIL_NORMALIZERS__?.normalizeSimpleOptions;
    const sizeAttribute = attributes.find((attribute) =>
      /^(?:尺码|服装尺码|可选尺码|鞋码|鞋子尺码)$/u.test(cleanText(attribute.label)));
    const sizeValues = typeof normalizeSimpleOptions === 'function'
      ? normalizeSimpleOptions(sizeAttribute?.value)
      : [];
    const dimensions = [];
    if (colorOptions.length > 0) {
      dimensions.push({
        name: '颜色',
        source: 'attribute:颜色',
        options: colorOptions.map((option, index) => ({
          optionKey: `color:${index + 1}`,
          sourceValue: option.sourceValue,
          normalizedValue: option.normalizedValue,
          status: option.status,
        })),
      });
    }
    if (sizeValues.length > 0) {
      dimensions.push({
        name: '尺码',
        source: `attribute:${sizeAttribute.label}`,
        options: sizeValues.map((value, index) => ({
          optionKey: `size:${index + 1}`,
          sourceValue: value,
          normalizedValue: value,
          status: 'normalized',
        })),
      });
    }

    if (colorOptions.length === 0 || sizeValues.length === 0) {
      return {
        status: 'dimensions_only',
        dimensions,
        combinations: [],
        diagnostics: {
          status: 'dimensions_only',
          reason: '页面没有同时提供颜色轴和尺码轴。',
          colorCount: colorOptions.length,
          sizeCount: sizeValues.length,
          verifiedColorCount: 0,
          combinationCount: 0,
        },
      };
    }

    const delayMs = normalizeLimit(options.skuInteractionDelayMs, 50, 1_000, 140);
    const combinations = [];
    let verifiedColorCount = 0;
    for (const [colorIndex, color] of colorOptions.entries()) {
      const colorElement = findSkuOptionElement([color.sourceValue, color.normalizedValue]);
      if (!colorElement || isUnavailableSkuOption(colorElement)) continue;
      clickSkuOption(colorElement);
      await delay(delayMs);
      verifiedColorCount += 1;

      for (const [sizeIndex, size] of sizeValues.entries()) {
        const sizeElement = findSkuOptionElement([size]);
        if (!sizeElement || isUnavailableSkuOption(sizeElement)) continue;
        const rowText = cleanText(findSkuOptionContainer(sizeElement)?.textContent);
        const stockMatch = rowText.match(/库存\s*(\d+)\s*(?:件|个)?/u);
        const priceMatch = rowText.match(/[¥￥]\s*(\d+(?:\.\d+)?)/u);
        combinations.push({
          combinationKey: `color:${colorIndex + 1}|size:${sizeIndex + 1}`,
          verification: 'dom-interaction',
          options: {
            color: color.normalizedValue,
            colorSourceValue: color.sourceValue,
            size,
          },
          stock: stockMatch ? Number(stockMatch[1]) : null,
          price: priceMatch ? Number(priceMatch[1]) : null,
        });
      }
    }

    const status = verifiedColorCount === colorOptions.length && combinations.length > 0
      ? 'verified'
      : combinations.length > 0
        ? 'partially_verified'
        : 'unverified';
    return {
      status,
      dimensions,
      combinations,
      diagnostics: {
        status,
        reason: combinations.length > 0
          ? '逐个选择颜色后读取当前可用尺码；未凭空补全组合。'
          : '未能从当前页面DOM确认颜色与尺码组合。',
        colorCount: colorOptions.length,
        sizeCount: sizeValues.length,
        verifiedColorCount,
        combinationCount: combinations.length,
      },
    };
  }

  function findSkuOptionElement(values) {
    const wanted = values.filter(Boolean).map((value) => cleanText(value));
    if (wanted.length === 0) return null;
    const selectors = [
      'button', '[role="button"]', '[role="option"]',
      '[class*="sku"]', '[class*="Sku"]', '[class*="prop"]', '[class*="Prop"]',
    ].join(',');
    return Array.from(document.querySelectorAll(selectors))
      .filter((element) => isElementVisible(element) && wanted.includes(cleanText(element.textContent)))
      .sort((left, right) => left.children.length - right.children.length)[0] ?? null;
  }

  function findSkuOptionContainer(element) {
    return element.closest(
      '.sku-item-wrapper, .sku-list-item, .expand-view-item, tr, [class*="sku-item"], [class*="SkuItem"]',
    ) || element;
  }

  function isUnavailableSkuOption(element) {
    const control = element.closest('button, [role="button"], [role="option"]') || element;
    const classText = `${control.className ?? ''} ${findSkuOptionContainer(control).className ?? ''}`;
    return Boolean(control.disabled)
      || control.getAttribute('aria-disabled') === 'true'
      || /(?:^|[-_\s])(disabled|disable|soldout|sold-out|unavailable)(?:$|[-_\s])/i.test(classText);
  }

  function clickSkuOption(element) {
    const control = element.closest('button, [role="button"], [role="option"]') || element;
    control.scrollIntoView?.({ block: 'center', inline: 'center' });
    control.click();
  }

  function readElementImageUrls(element) {
    const values = [
      element.currentSrc,
      element.getAttribute?.('src'),
      element.getAttribute?.('data-src'),
      element.getAttribute?.('data-lazy-src'),
      element.getAttribute?.('data-original'),
      readInlineBackgroundImageUrl(element),
    ];
    const srcset = element.getAttribute?.('srcset');
    if (srcset) {
      values.push(...srcset.split(',').map((item) => item.trim().split(/\s+/)[0]));
    }
    return values.filter(Boolean);
  }

  function readInlineBackgroundImageUrl(element) {
    const value = String(element?.style?.backgroundImage ?? '').trim();
    const match = value.match(/^url\(\s*(["']?)(.*?)\1\s*\)$/i);
    return match?.[2]?.trim() || null;
  }

  function normalizeProductImageUrl(value) {
    if (!value) return null;
    try {
      const url = new URL(value, document.baseURI);
      if (url.protocol !== 'https:') return null;
      if (!url.hostname.endsWith('alicdn.com')) return null;
      if (/\.(?:svg|gif)(?:$|\?)/i.test(url.pathname)) return null;
      if (/sprite|icon|logo|avatar/i.test(url.pathname)) return null;
      return url.href;
    } catch {
      return null;
    }
  }

  function collectVisibleTexts(selector, pattern, limit) {
    const result = [];
    const seen = new Set();
    for (const element of document.querySelectorAll(selector)) {
      if (!isElementVisible(element)) continue;
      const value = cleanText(element.textContent);
      if (!value || value.length > 500 || !pattern.test(value) || seen.has(value)) continue;
      seen.add(value);
      result.push(value);
      if (result.length >= limit) break;
    }
    return result;
  }

  function isElementVisible(element) {
    const style = getComputedStyle(element);
    if (style.display === 'none' || style.visibility === 'hidden') return false;
    const rect = element.getBoundingClientRect();
    return rect.width > 0 && rect.height > 0;
  }

  function countSources(facts) {
    const result = {};
    for (const fact of facts) {
      result[fact.source] = (result[fact.source] ?? 0) + 1;
    }
    return result;
  }

  function extractOfferId(value) {
    try {
      const url = new URL(value);
      const queryValue = url.searchParams.get('offerId');
      if (/^\d+$/.test(queryValue ?? '')) return queryValue;
      return url.pathname.match(/\/offer\/(\d+)/)?.[1] ?? null;
    } catch {
      return null;
    }
  }

  function normalizeProductTitle(value) {
    return cleanText(value).replace(/\s*-\s*阿里巴巴\s*$/u, '') || null;
  }

  function isUsablePair(label, value) {
    if (!label || !value || label === value) return false;
    if (label.length > MAX_LABEL_LENGTH || value.length > MAX_VALUE_LENGTH) return false;
    if (/^(https?:)?\/\//i.test(label)) return false;
    return normalizeIdentity(label).length >= 2;
  }

  function cleanText(value) {
    return String(value ?? '').replace(/\s+/g, ' ').trim();
  }

  function normalizeIdentity(value) {
    return cleanText(value).normalize('NFKC').toLocaleLowerCase();
  }

  function truncate(value, maxLength) {
    return value.length <= maxLength ? value : `${value.slice(0, maxLength - 1)}…`;
  }

  function normalizeLimit(value, minimum, maximum, fallback) {
    const normalized = Number(value);
    if (!Number.isInteger(normalized)) return fallback;
    return Math.min(maximum, Math.max(minimum, normalized));
  }

  function delay(ms) {
    return new Promise((resolve) => setTimeout(resolve, ms));
  }
})();
