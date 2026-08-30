export const HOME_URL = 'https://www.1688.com/';
export const DEBUGGER_PROTOCOL_VERSION = '1.3';

export const SELECTORS = Object.freeze({
  searchInput: '#alisearch-input',
  productCard: '.search-offer-item',
  productLink: ':scope > a[href], a[href]',
  productImage: '.main-img',
  productTitle: '.title-text',
  productPrice: '.text-main',
  priceFilterForm: '.price-filter-form',
  activePriceFilterForm: '.price-filter-form.form-submit',
  priceMinimumInput: '.form-input:not(.price-filter-end)',
  priceMaximumInput: '.form-input.price-filter-end',
  priceConfirmButton: '.price-filter-define > .sn-common-button-dpl-define',
  priceAscendingSort: '.sw-dpl-orderby-asc',
  sortItem: '.sm-sort-item',
});

export const SORT_MODES = Object.freeze({
  sales: 'sales',
  priceAscending: 'priceAscending',
});

export const LIMITS = Object.freeze({
  maxItems: 60,
  pageLoadTimeoutMs: 30_000,
  searchResultTimeoutMs: 20_000,
  productRenderTimeoutMs: 60_000,
  wheelDownStepPx: 600,
  wheelStepDelayMs: 250,
  filterApplyDelayMs: 500,
  filterControlTimeoutMs: 3_000,
});

export const DETAIL_DOM_OPTIONS = Object.freeze({
  defaultItemIndex: 1,
  maxHtmlBytes: 5_000_000,
  maxDepth: 12,
  maxNodes: 2_000,
  maxAttributes: 10,
  maxTextLength: 200,
  maxAttributeValueLength: 160,
  downloadTimeoutMs: 30_000,
});

export function isAllowed1688Url(value) {
  try {
    const url = new URL(value);
    return (
      ['http:', 'https:'].includes(url.protocol) &&
      (url.hostname === '1688.com' || url.hostname.endsWith('.1688.com'))
    );
  } catch {
    return false;
  }
}

export function isSearchResultUrl(value) {
  if (!isAllowed1688Url(value)) return false;
  const url = new URL(value);
  return (
    url.hostname === 's.1688.com' &&
    url.pathname.includes('/selloffer/offer_search.htm')
  );
}

export function normalizeSearchKeyword(value) {
  return String(value ?? '').trim();
}

export function areSearchKeywordsEqual(currentValue, targetValue) {
  return normalizeSearchKeyword(currentValue) === normalizeSearchKeyword(targetValue);
}

export function selectReusableSearchResultTab(tabs) {
  if (!Array.isArray(tabs)) return null;
  const resultTabs = tabs.filter((tab) => isSearchResultUrl(tab?.url));
  return resultTabs.find((tab) => tab.active) ?? resultTabs[0] ?? null;
}

export function normalizeStartRequest(message) {
  if (message?.type !== 'START_1688_DOM_DEMO') {
    throw new TypeError('不支持的任务类型。');
  }

  const keyword = normalizeSearchKeyword(message.keyword);
  if (!keyword) throw new TypeError('搜索词不能为空。');
  if (keyword.length > 100) throw new TypeError('搜索词不能超过100个字符。');

  const requestedMaxItems = Number(message.maxItems ?? LIMITS.maxItems);
  if (!Number.isInteger(requestedMaxItems) || requestedMaxItems < 1) {
    throw new TypeError('maxItems 必须是正整数。');
  }

  const procurementMinimumCny = normalizeOptionalPrice(
    message.procurementMinimumCny,
    '采购成本下限',
  );
  const procurementMaximumCny = normalizeOptionalPrice(
    message.procurementMaximumCny,
    '采购成本上限',
  );
  if ((procurementMinimumCny === null) !== (procurementMaximumCny === null)) {
    throw new TypeError('采购成本上下限必须同时提供。');
  }
  if (
    procurementMinimumCny !== null &&
    procurementMinimumCny > procurementMaximumCny
  ) {
    throw new TypeError('采购成本下限不能高于上限。');
  }

  const sortMode = String(message.sortMode ?? SORT_MODES.sales);
  if (!Object.values(SORT_MODES).includes(sortMode)) {
    throw new TypeError('商品排序模式无效。');
  }

  return {
    keyword,
    maxItems: Math.min(requestedMaxItems, LIMITS.maxItems),
    includeDetailDom: message.includeDetailDom !== false,
    procurementMinimumCny,
    procurementMaximumCny,
    sortMode,
  };
}

function normalizeOptionalPrice(value, fieldName) {
  if (value === undefined || value === null || value === '') return null;
  const price = Number(value);
  if (!Number.isFinite(price) || price < 0) {
    throw new TypeError(`${fieldName}必须是非负数字。`);
  }
  return price;
}
