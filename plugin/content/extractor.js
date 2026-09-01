(() => {
  if (globalThis.__AUTO_MAGIC_1688_EXTRACTOR__) return;
  globalThis.__AUTO_MAGIC_1688_EXTRACTOR__ = true;

  chrome.runtime.onMessage.addListener((message, _sender, sendResponse) => {
    if (message?.type !== 'EXTRACT_1688_PRODUCTS') return false;

    try {
      sendResponse({
        success: true,
        data: extractProducts(message.maxItems, message.selectors),
      });
    } catch (error) {
      sendResponse({ success: false, error: error.message });
    }
    return false;
  });

  function extractProducts(maxItems, selectors) {
    const clean = (value) => value?.replace(/\s+/g, ' ').trim() || null;
    const normalizeHttpsUrl = (value, hostnameFilter = null) => {
      if (!value) return null;
      try {
        const url = new URL(value, document.baseURI);
        if (url.protocol !== 'https:') return null;
        if (hostnameFilter && !hostnameFilter(url.hostname)) return null;
        return url.href;
      } catch {
        return null;
      }
    };
    const extractImageUrl = (image) => {
      if (!image) return null;
      const candidates = [
        image.currentSrc,
        image.getAttribute('src'),
        image.getAttribute('data-src'),
        image.getAttribute('data-lazy-src'),
        image.getAttribute('data-original'),
      ];
      for (const source of candidates) {
        const normalized = normalizeHttpsUrl(source);
        if (normalized) return normalized;
      }
      return null;
    };
    const is1688Hostname = (hostname) =>
      hostname === '1688.com' || hostname.endsWith('.1688.com');
    const requiredSelectors = [
      'productCard',
      'productLink',
      'productImage',
      'productTitle',
      'productPrice',
    ];
    if (
      !selectors ||
      requiredSelectors.some(
        (key) => typeof selectors[key] !== 'string' || !selectors[key].trim(),
      )
    ) {
      throw new TypeError('商品解析选择器配置不完整。');
    }

    const limit = Number.isInteger(maxItems) && maxItems > 0 ? maxItems : 60;

    const cards = [...document.querySelectorAll(selectors.productCard)];
    const products = new Map();
    const seenDetailUrls = new Set();
    const skipped = {
      missingDetailUrl: 0,
      malformedDetailUrl: 0,
      nonHttpsDetailUrl: 0,
      non1688DetailUrl: 0,
      nonOfferDetailUrl: 0,
      duplicateDetailUrl: 0,
      overLimit: 0,
    };
    const invalidDetailUrlSamples = [];
    const unresolvedDetailCards = [];
    let validDetailUrlCount = 0;
    let upgradedHttpDetailUrlCount = 0;

    for (const [cardIndex, card] of cards.entries()) {
      const detailUrlResult = findDetailUrl(card);
      if (!detailUrlResult.url) {
        skipped[detailUrlResult.reason] += 1;
        if (invalidDetailUrlSamples.length < 3) {
          invalidDetailUrlSamples.push({
            reason: detailUrlResult.reason,
            href: clean(detailUrlResult.rawUrl)?.slice(0, 200) ?? null,
          });
        }
        if (detailUrlResult.reason !== 'nonOfferDetailUrl') continue;
      }

      const detailUrl = detailUrlResult.url;
      if (detailUrl) {
        validDetailUrlCount += 1;
        if (detailUrlResult.upgradedFromHttp) upgradedHttpDetailUrlCount += 1;
        if (seenDetailUrls.has(detailUrl)) {
          skipped.duplicateDetailUrl += 1;
          continue;
        }
        seenDetailUrls.add(detailUrl);
      }
      if (products.size >= limit) {
        skipped.overLimit += 1;
        continue;
      }

      const title = clean(card.querySelector(selectors.productTitle)?.textContent);
      const priceCny = clean(card.querySelector(selectors.productPrice)?.textContent);
      const image = card.querySelector(selectors.productImage);

      const itemIndex = products.size;
      products.set(detailUrl ?? `unresolved:${cardIndex}`, {
        detailUrl,
        imageUrl: extractImageUrl(image),
        title,
        priceCny,
      });
      if (!detailUrl) {
        unresolvedDetailCards.push({
          cardIndex,
          itemIndex,
          title,
          reason: detailUrlResult.reason,
        });
      }
    }

    function findDetailUrl(card) {
      const links = [];
      if (card.matches?.('a[href]')) links.push(card);
      for (const link of card.querySelectorAll?.('a[href]') ?? []) links.push(link);
      if (!links.length) return { url: null, rawUrl: null, reason: 'missingDetailUrl' };

      const failures = [];
      for (const link of links) {
        const rawUrl = link?.href || link?.getAttribute?.('href');
        const parsed = parseDetailUrl(rawUrl);
        if (parsed.url) return { ...parsed, rawUrl };
        failures.push({ ...parsed, rawUrl });
      }
      return failures.find((failure) => failure.reason === 'nonOfferDetailUrl')
        ?? failures[0]
        ?? { url: null, rawUrl: null, reason: 'missingDetailUrl' };
    }

    function parseDetailUrl(value) {
      if (!value) return { url: null, reason: 'missingDetailUrl' };
      try {
        const url = new URL(value, document.baseURI);
        let upgradedFromHttp = false;
        if (url.protocol === 'http:' && is1688Hostname(url.hostname)) {
          url.protocol = 'https:';
          upgradedFromHttp = true;
        }
        if (url.protocol !== 'https:') {
          return { url: null, reason: 'nonHttpsDetailUrl' };
        }
        if (!is1688Hostname(url.hostname)) {
          return { url: null, reason: 'non1688DetailUrl' };
        }
        if (!isOfferDetailUrl(url)) {
          return { url: null, reason: 'nonOfferDetailUrl' };
        }
        return { url: url.href, reason: null, upgradedFromHttp };
      } catch {
        return { url: null, reason: 'malformedDetailUrl' };
      }
    }

    function isOfferDetailUrl(url) {
      if (/\/offer\/\d+(?:\.html)?(?:\/|$)/i.test(url.pathname)) return true;
      return (
        ['detail.m.1688.com', 'm.1688.com'].includes(url.hostname) &&
        /^\d+$/.test(url.searchParams.get('offerId') ?? '')
      );
    }

    return {
      sourceUrl: location.href,
      pageTitle: document.title,
      capturedAt: new Date().toISOString(),
      count: products.size,
      items: [...products.values()],
      diagnostics: {
        rawCardCount: cards.length,
        validDetailUrlCount,
        uniqueValidDetailUrlCount: seenDetailUrls.size,
        upgradedHttpDetailUrlCount,
        outputCount: products.size,
        skipped,
        invalidDetailUrlSamples,
        unresolvedDetailCards,
      },
    };
  }
})();
