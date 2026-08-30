import assert from 'node:assert/strict';
import { readFile } from 'node:fs/promises';
import test from 'node:test';

const configSource = await readFile(
  new URL('../plugin/lib/config.js', import.meta.url),
  'utf8',
);
const config = await import(
  `data:text/javascript;base64,${Buffer.from(configSource).toString('base64')}`
);

test('compares the current and target keywords after trimming outer whitespace', () => {
  assert.equal(config.areSearchKeywordsEqual('  连衣裙 ', '连衣裙'), true);
  assert.equal(config.areSearchKeywordsEqual('女装', '连衣裙'), false);
  assert.equal(config.areSearchKeywordsEqual('', '连衣裙'), false);
});

test('selects the active reusable result tab from the current window', () => {
  const tabs = [
    { id: 1, active: true, url: 'https://www.1688.com/' },
    {
      id: 2,
      active: false,
      url: 'https://s.1688.com/selloffer/offer_search.htm?keywords=old',
    },
    {
      id: 3,
      active: true,
      url: 'https://s.1688.com/selloffer/offer_search.htm?keywords=current',
    },
  ];

  assert.equal(config.selectReusableSearchResultTab(tabs)?.id, 3);
});

test('returns null when the current window has no reusable result tab', () => {
  const tabs = [
    { id: 1, active: true, url: 'https://www.1688.com/' },
    { id: 2, active: false, url: 'https://example.com/' },
  ];

  assert.equal(config.selectReusableSearchResultTab(tabs), null);
});

test('uses the requested downward mouse-wheel distance and timing', () => {
  assert.equal(config.SELECTORS.productCard, '.search-offer-item');
  assert.equal(config.LIMITS.wheelDownStepPx, 600);
  assert.equal(config.LIMITS.wheelStepDelayMs, 250);
});

test('keeps the active price form as a hint and uses a semantic confirm selector', () => {
  assert.equal(
    config.SELECTORS.activePriceFilterForm,
    '.price-filter-form.form-submit',
  );
  assert.equal(
    config.SELECTORS.priceConfirmButton,
    '.price-filter-define > .sn-common-button-dpl-define',
  );
});

test('defaults detail DOM capture to the second list item with bounded output', () => {
  assert.equal(config.DETAIL_DOM_OPTIONS.defaultItemIndex, 1);
  assert.equal(config.DETAIL_DOM_OPTIONS.maxHtmlBytes, 5_000_000);
  assert.equal(config.DETAIL_DOM_OPTIONS.maxNodes, 2_000);
  assert.equal(config.DETAIL_DOM_OPTIONS.maxDepth, 12);
});

test('keeps detail DOM for popup searches and allows desktop searches to skip it', () => {
  const popupRequest = config.normalizeStartRequest({
    type: 'START_1688_DOM_DEMO',
    keyword: '连衣裙',
  });
  const desktopRequest = config.normalizeStartRequest({
    type: 'START_1688_DOM_DEMO',
    keyword: '连衣裙',
    includeDetailDom: false,
  });

  assert.equal(popupRequest.includeDetailDom, true);
  assert.equal(desktopRequest.includeDetailDom, false);
});
