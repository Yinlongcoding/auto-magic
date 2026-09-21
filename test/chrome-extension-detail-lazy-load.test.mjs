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

test('detail collection reads the initial DOM and only performs a targeted retry when needed', () => {
  assert.match(detailExtractor, /PREPARE_1688_TARGETED_DETAIL/);
  assert.match(detailExtractor, /prepareTargetedContent/);
  assert.match(detailExtractor, /scrollIntoView/);
  assert.match(detailExtractor, /window\.scrollTo\(\{ top: 0/);
  assert.match(detailExtractor, /fullPageScrolled: false/);
  assert.doesNotMatch(detailExtractor, /reachedBottom/);
  assert.doesNotMatch(background, /PREPARE_1688_RENDERED_DETAIL/);
  assert.match(background, /needAttributes \|\| needSku/);
});

test('new detail source payload does not duplicate canonical facts in raw fields', () => {
  assert.doesNotMatch(detailExtractor, /raw:\s*\{[\s\S]*?\n\s*title,/);
  assert.doesNotMatch(detailExtractor, /raw:\s*\{[\s\S]*?\n\s*attributes,/);
  assert.doesNotMatch(detailExtractor, /raw:\s*\{[\s\S]*?\n\s*colorOptions,/);
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

test('detail extraction accepts only structurally complete observed SKU combinations', () => {
  assert.match(detailExtractor, /skuDimensions/);
  assert.match(detailExtractor, /skuCombinations/);
  assert.match(detailExtractor, /verification: 'structured-json'/);
  assert.doesNotMatch(detailExtractor, /verification: 'dom-interaction'/);
  assert.doesNotMatch(detailExtractor, /verification: 'cartesian'/);
  assert.match(background, /captureStructuredSkuFromPage/);
  assert.match(background, /world: 'MAIN'/);
  assert.match(background, /structured-sku-reader\.js/);
  assert.match(detailExtractor, /excludedCombinations/);
  assert.match(detailExtractor, /invalid_structure/);
  assert.match(detailExtractor, /规格组合结构不完整或缺少规格轴/);
  assert.match(background, /unresolvedSkuEvidence/);
  assert.match(background, /页面存在SKU证据，但未取得可追溯的真实SKU组合/);
});

test('detail extraction does not infer category semantics or click options to build combinations', () => {
  assert.doesNotMatch(detailExtractor, /normalizeColorOptions/);
  assert.doesNotMatch(detailExtractor, /normalizeSizeOptions/);
  assert.doesNotMatch(detailExtractor, /findSkuOptionElements/);
  assert.doesNotMatch(detailExtractor, /clickSkuOption/);
});
