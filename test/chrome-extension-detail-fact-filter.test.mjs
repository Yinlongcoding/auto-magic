import assert from 'node:assert/strict';
import test from 'node:test';

await import('../plugin/lib/detail-fact-filter.js');

const { filterMatrixFacts } = globalThis.__AUTO_MAGIC_DETAIL_FACT_FILTER__;

test('removes color-by-weight matrix rows without deleting the canonical color fact', () => {
  const facts = filterMatrixFacts([
    { label: '颜色', value: '粉红色,藏青色,黑色', source: 'semantic-table' },
    { label: '颜色', value: '重量(g)', source: 'semantic-table' },
    { label: '粉红色', value: '380', source: 'semantic-table' },
    { label: '藏青色', value: '380', source: 'semantic-table' },
    { label: '黑色', value: '380', source: 'semantic-table' },
    { label: '商家代发热度', value: '506', source: 'semantic-table' },
  ]);

  assert.deepEqual(facts, [
    { label: '颜色', value: '粉红色,藏青色,黑色', source: 'semantic-table' },
    { label: '商家代发热度', value: '506', source: 'semantic-table' },
  ]);
});

test('keeps numeric option facts when no matrix header proves their relationship', () => {
  const facts = [{ label: '9号', value: '380', source: 'semantic-table' }];
  assert.deepEqual(filterMatrixFacts(facts), facts);
});
