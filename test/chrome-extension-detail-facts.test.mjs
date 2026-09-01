import test from 'node:test';
import assert from 'node:assert/strict';

import {
  extractJsonValuesFromScriptText,
  normalizeFactCandidates,
} from '../plugin/lib/detail-facts.js';

test('detail facts keep dynamic fields and remove exact duplicates', () => {
  const facts = normalizeFactCandidates([
    { label: '材质', value: '棉', source: 'dom-pair' },
    { label: '材质', value: '棉', source: 'embedded-json' },
    { label: '保质期', value: '12个月', source: 'dom-pair' },
    { label: '执行标准', value: 'GB/T 1234', source: 'dom-pair' },
  ]);

  assert.deepEqual(facts, [
    { label: '材质', value: '棉', source: 'dom-pair' },
    { label: '保质期', value: '12个月', source: 'dom-pair' },
    { label: '执行标准', value: 'GB/T 1234', source: 'dom-pair' },
  ]);
});

test('detail facts enforce a configurable safety cap', () => {
  const facts = normalizeFactCandidates([
    { label: '字段一', value: '值一', source: 'dom' },
    { label: '字段二', value: '值二', source: 'dom' },
  ], 1);

  assert.equal(facts.length, 1);
});

test('detail facts parse static assigned JSON without executing page scripts', () => {
  globalThis.__AUTO_MAGIC_SHOULD_NOT_RUN__ = false;
  const values = extractJsonValuesFromScriptText(
    'window.__DETAIL__ = {"材质":"棉","保质期":"12个月"}; globalThis.__AUTO_MAGIC_SHOULD_NOT_RUN__ = true;',
  );

  assert.deepEqual(values, [{ 材质: '棉', 保质期: '12个月' }]);
  assert.equal(globalThis.__AUTO_MAGIC_SHOULD_NOT_RUN__, false);
  delete globalThis.__AUTO_MAGIC_SHOULD_NOT_RUN__;
});
