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
    if (message?.type === 'PREPARE_1688_TARGETED_DETAIL') {
      void prepareTargetedContent(message.options).then(
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

    const filterMatrixFacts = globalThis.__AUTO_MAGIC_DETAIL_FACT_FILTER__?.filterMatrixFacts;
    const attributes = normalizeCandidates(
      typeof filterMatrixFacts === 'function' ? filterMatrixFacts(candidates) : candidates,
      maxFacts,
    );
    const skuCapture = collectStructuredSkuCapture(options);
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

    const targetedPreparation = globalThis.__AUTO_MAGIC_DETAIL_TARGETED_STATE__ ?? null;
    return {
      ready: !blocked,
      finalUrl: location.href,
      capturedAt: new Date().toISOString(),
      pageTitle: document.title || null,
      facts,
      raw: {
        offerId: extractOfferId(location.href),
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
        acquisition: {
          mode: targetedPreparation ? 'targeted-retry' : 'initial-dom',
          fullPageScrolled: false,
          targetedPreparation,
        },
      },
    };
  }

  async function prepareTargetedContent(options = {}) {
    const stepDelayMs = normalizeLimit(options.stepDelayMs, 50, 2_000, 180);
    const returnTopSettleMs = normalizeLimit(options.returnTopSettleMs, 50, 2_000, 200);
    const startedAt = Date.now();
    const initialHeight = getDocumentHeight();
    if (isBlockedPage()) throw new Error('1688详情页触发了验证码或访问限制。');
    const selectors = [];
    if (options.needSku) {
      selectors.push(
        '[class*="sku"]', '[class*="Sku"]', '[class*="spec"]', '[class*="Spec"]',
      );
    }
    if (options.needAttributes) {
      selectors.push('.decision-attributes-list', '.normal-attributes-table', '.cpv-attr-item');
    }
    const targets = [...new Set(selectors
      .map((selector) => document.querySelector(selector))
      .filter(Boolean))];
    for (const target of targets) {
      if (isBlockedPage()) throw new Error('1688详情页触发了验证码或访问限制。');
      target.scrollIntoView({ block: 'center', inline: 'nearest', behavior: 'instant' });
      await delay(stepDelayMs);
    }
    window.scrollTo({ top: 0, behavior: 'instant' });
    await delay(returnTopSettleMs);
    const state = {
      completed: true,
      elapsedMs: Date.now() - startedAt,
      requestedSku: Boolean(options.needSku),
      requestedAttributes: Boolean(options.needAttributes),
      targetCount: targets.length,
      initialHeight,
      finalHeight: getDocumentHeight(),
      signature: readPageSignature(),
    };
    globalThis.__AUTO_MAGIC_DETAIL_TARGETED_STATE__ = state;
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

  function readPageSignature() {
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

  function collectStructuredSkuCapture(options) {
    const structured = normalizeStructuredSkuCapture(options.structuredSkuCapture);
    if (structured.combinations.length > 0) {
      const expectedDimensions = structured.dimensions.map((dimension) => dimension.name);
      const accepted = [];
      const excluded = [];
      for (const combination of structured.combinations) {
        const optionNames = Object.keys(combination.options);
        const structureComplete = optionNames.length > 0
          && optionNames.length === new Set(optionNames).size
          && expectedDimensions.every((name) => optionNames.includes(name));
        if (structureComplete) accepted.push(combination);
        else excluded.push({
          combinationKey: combination.combinationKey,
          sourceSkuId: combination.skuId,
          reason: '规格组合结构不完整或缺少规格轴。',
        });
      }
      const inferred = accepted.length > 0 && accepted.every((combination) =>
        combination.verification === 'dimension-cartesian');
      const status = accepted.length === structured.combinations.length
        ? inferred ? 'dimension_cartesian' : 'verified'
        : accepted.length > 0 ? 'partially_verified' : 'invalid_structure';
      return {
        status,
        dimensions: structured.dimensions,
        combinations: accepted,
        diagnostics: {
          ...structured.diagnostics,
          status,
          strategy: inferred ? 'dimension-cartesian' : 'structured-json',
          reason: excluded.length === 0
            ? inferred
              ? '根据页面规格轴枚举SKU组合；组合库存和价格尚未由页面逐项确认。'
              : '已从页面上下文结构化数据还原真实SKU组合。'
            : '只保留来源可追溯且规格结构完整的真实SKU组合。',
          dimensionCount: structured.dimensions.length,
          observedCombinationCount: structured.combinations.length,
          combinationCount: accepted.length,
          excludedCombinationCount: excluded.length,
          excludedCombinations: excluded,
        },
      };
    }
    const status = structured.dimensions.length > 0 ? 'dimensions_only' : 'unverified';
    return {
      status,
      dimensions: structured.dimensions,
      combinations: [],
      diagnostics: {
        status,
        strategy: 'dimension-evidence-only',
        structuredProbe: structured.diagnostics,
        reason: '结构化页面数据未提供可追溯的真实SKU组合。',
        dimensionCount: structured.dimensions.length,
        combinationCount: 0,
      },
    };
  }

  function normalizeStructuredSkuCapture(capture) {
    const diagnostics = capture?.diagnostics && typeof capture.diagnostics === 'object'
      ? capture.diagnostics
      : { strategy: 'page-context-structured-data', reason: '没有结构化SKU探针结果。' };
    const dimensions = Array.isArray(capture?.dimensions)
      ? capture.dimensions.map((dimension) => {
          const name = cleanText(dimension?.name);
          const options = Array.isArray(dimension?.options)
            ? dimension.options.map((option, index) => {
                const sourceValue = cleanText(option?.sourceValue);
                return {
                  optionKey: cleanText(option?.optionKey) || `${name}:${index + 1}`,
                  sourceOptionId: cleanText(option?.sourceOptionId) || null,
                  sourceValue,
                  normalizedValue: sourceValue,
                  status: 'observed',
                  imageUrl: cleanText(option?.imageUrl) || null,
                };
              }).filter((option) => option.sourceValue)
            : [];
          return {
            name,
            source: cleanText(dimension?.source) || 'structured-json',
            sourceDimensionId: cleanText(dimension?.sourceDimensionId) || null,
            options,
          };
        }).filter((dimension) => dimension.name && dimension.options.length > 0)
      : [];
    const combinations = Array.isArray(capture?.combinations)
      ? capture.combinations.map((combination, index) => {
          const capturedOptions = combination?.options && typeof combination.options === 'object'
            ? Object.fromEntries(Object.entries(combination.options)
                .map(([name, value]) => [cleanText(name), cleanText(value)])
                .filter(([name, value]) => name && value))
            : {};
          const optionIds = combination?.optionIds && typeof combination.optionIds === 'object'
            ? Object.fromEntries(Object.entries(combination.optionIds)
                .map(([name, value]) => [cleanText(name), cleanText(value)])
                .filter(([name, value]) => name && value))
            : {};
          return {
            skuId: cleanText(combination?.skuId) || null,
            combinationKey: cleanText(combination?.combinationKey) || `structured:${index + 1}`,
            verification: cleanText(combination?.verification) === 'dimension-cartesian'
              ? 'dimension-cartesian' : 'structured-json',
            options: capturedOptions,
            optionIds,
            price: optionalNumber(combination?.price),
            stock: optionalInteger(combination?.stock),
            availability: cleanText(combination?.availability) || 'unknown',
            imageUrl: cleanText(combination?.imageUrl) || null,
            sourcePath: cleanText(combination?.sourcePath) || null,
          };
        }).filter((combination) => Object.keys(combination.options).length > 0)
      : [];
    return { dimensions, combinations, diagnostics };
  }

  function optionalNumber(value) {
    if (value === null || value === undefined || value === '') return null;
    const number = Number(value);
    return Number.isFinite(number) ? number : null;
  }

  function optionalInteger(value) {
    const number = optionalNumber(value);
    return Number.isInteger(number) ? number : null;
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
