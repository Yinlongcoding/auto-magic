import assert from 'node:assert/strict';
import { readFile } from 'node:fs/promises';
import test from 'node:test';

const detailExtractor = await readFile(
  new URL('../plugin/content/detail-extractor.js', import.meta.url),
  'utf8',
);
const background = await readFile(
  new URL('../plugin/background.js', import.meta.url),
  'utf8',
);

test('detail preparation scrolls to the bottom, settles once, then returns to the top', () => {
  assert.match(detailExtractor, /PREPARE_1688_RENDERED_DETAIL/);
  assert.match(detailExtractor, /bottomSettleMs/);
  assert.match(detailExtractor, /stillAtBottom && settledHeight === currentHeight/);
  assert.match(detailExtractor, /window\.scrollTo\(\{ top: 0/);
  assert.match(detailExtractor, /lazyLoad\?\.completed === true/);
});

test('detail queue recreates missing shared tabs and classifies retryable failures', () => {
  assert.match(background, /ensureReusableDetailTab/);
  assert.match(background, /failureCode === 'tab_lost'/);
  assert.match(background, /shouldRetryDetail/);
  assert.match(background, /\['tab_lost', 'captcha_blocked', 'network_error'/);
  assert.match(background, /waitForDetailDomReady/);
  assert.doesNotMatch(background, /waitForTabComplete\(tabId, remainingTimeout/);
});

test('detail cache uses offer id plus collector version instead of full url', () => {
  assert.match(background, /getDetailCacheKey/);
  assert.match(background, /DETAIL_FACT_OPTIONS\.cacheVersion/);
});

test('detail extraction records dimensions and only DOM-verified SKU combinations', () => {
  assert.match(detailExtractor, /skuDimensions/);
  assert.match(detailExtractor, /skuCombinations/);
  assert.match(detailExtractor, /verification: 'dom-interaction'/);
  assert.match(detailExtractor, /未凭空补全组合/);
  assert.doesNotMatch(detailExtractor, /verification: 'cartesian'/);
});
