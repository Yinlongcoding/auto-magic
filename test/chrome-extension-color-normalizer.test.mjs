import assert from 'node:assert/strict';
import test from 'node:test';

await import('../plugin/lib/detail-normalizers.js');

const { normalizeColorOptions, normalizeSimpleOptions } = globalThis.__AUTO_MAGIC_DETAIL_NORMALIZERS__;

test('normalizes numbered 1688 color options and inventory suffixes', () => {
  const options = normalizeColorOptions(
    '1绿色有现货、8浅红有现货、10酒红有现货、11藏青色、18墨绿',
  );

  assert.deepEqual(options.map((option) => option.normalizedValue), [
    '绿色', '浅红', '酒红', '藏青色', '墨绿',
  ]);
});

test('normalizes a simple size axis without inventing combinations', () => {
  assert.deepEqual(normalizeSimpleOptions('45、47、48'), ['45', '47', '48']);
});

test('keeps nameless color options unresolved instead of guessing', () => {
  const options = normalizeColorOptions('6有现货、7现货、9库存20件');

  assert.deepEqual(options.map((option) => option.status), [
    'unresolved', 'unresolved', 'unresolved',
  ]);
  assert.deepEqual(options.map((option) => option.normalizedValue), [null, null, null]);
});
