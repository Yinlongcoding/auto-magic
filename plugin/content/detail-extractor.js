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
    if (message?.type !== 'EXTRACT_1688_RENDERED_DETAIL') return false;

    try {
      sendResponse({
        success: true,
        data: extractRenderedDetail(message.options),
      });
    } catch (error) {
      sendResponse({ success: false, error: error.message });
    }
    return false;
  });

  function extractRenderedDetail(options = {}) {
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
    const title = normalizeProductTitle(document.title);
    const facts = normalizeCandidates([
      ...(title ? [{ label: '商品名称', value: title, source: 'document.title' }] : []),
      ...attributes,
    ], maxFacts);
    const imageCapture = collectProductImageUrls(maxImages);
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

    return {
      ready: attributes.length > 0 && !blocked,
      finalUrl: location.href,
      capturedAt: new Date().toISOString(),
      pageTitle: document.title || null,
      facts,
      raw: {
        offerId: extractOfferId(location.href),
        title,
        attributes,
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
        sources: sourceCounts,
        selectors: {
          decisionAttributes: document.querySelectorAll('.decision-attributes-list > li').length,
          normalAttributes: document.querySelectorAll('.normal-attributes-table tbody tr').length,
          cpvAttributes: document.querySelectorAll('.cpv-attr-item').length,
        },
      },
    };
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

  function collectProductImageUrls(limit) {
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

    return {
      urls: [...urls],
      diagnostics: {
        selector,
        source: 'inline-background-image',
        candidateCount: galleryItems.length,
        excludedLastItemCount: galleryItems.length > 0 ? 1 : 0,
        inspectedCount: productItems.length,
        invalidBackgroundImageCount,
        duplicateCount,
        outputCount: urls.size,
      },
    };
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
})();
