import assert from 'node:assert/strict';
import test from 'node:test';

await import('../plugin/lib/structured-sku-reader.js');

const { capture, extractFromObject, parseStaticJsonValues } =
  globalThis.__AUTO_MAGIC_STRUCTURED_SKU_READER__;

test('reads real skuBase and skuCore combinations without creating a cartesian product', () => {
  const captured = extractFromObject({
    skuBase: {
      props: [
        {
          pid: '1',
          name: '颜色',
          values: [
            { vid: '11', name: '白色', imageUrl: 'https://cbu01.alicdn.com/white.jpg' },
            { vid: '12', name: '黑色', imageUrl: 'https://cbu01.alicdn.com/black.jpg' },
          ],
        },
        {
          pid: '2',
          name: '尺码',
          values: [
            { vid: '21', name: 'S' },
            { vid: '22', name: 'M' },
          ],
        },
      ],
    },
    skuCore: {
      sku2info: {
        '1:11;2:21': { skuId: 'sku-white-s', price: '35.50', quantity: 12 },
        '1:12;2:22': { skuId: 'sku-black-m', price: '36', quantity: 0 },
        '$default': { price: '30-40' },
      },
    },
  });

  assert.ok(captured);
  assert.equal(captured.dimensions.length, 2);
  assert.equal(captured.combinations.length, 2);
  assert.deepEqual(captured.combinations[0].options, { 颜色: '白色', 尺码: 'S' });
  assert.equal(captured.combinations[0].skuId, 'sku-white-s');
  assert.equal(captured.combinations[0].price, 35.5);
  assert.equal(captured.combinations[0].stock, 12);
  assert.equal(captured.combinations[1].availability, 'unavailable');
});

test('reads skuProps and skuInfoMap shape with one real dimension', () => {
  const captured = extractFromObject({
    skuProps: [{
      propId: '1627207',
      propName: '颜色分类',
      values: [
        { valueId: '28320', valueName: '黄色' },
        { valueId: '28321', valueName: '绿色' },
      ],
    }],
    skuInfoMap: {
      '1627207:28321': { offerSkuId: 9988, salePrice: 18.8, stock: 6 },
    },
  }, '$.skuModel');

  assert.ok(captured);
  assert.equal(captured.combinations.length, 1);
  assert.deepEqual(captured.combinations[0].options, { 颜色分类: '绿色' });
  assert.equal(captured.combinations[0].skuId, '9988');
});

test('joins skuBase skus with skuCore info keyed by source sku id', () => {
  const captured = extractFromObject({
    skuBase: {
      props: [
        { pid: '1', name: '颜色', values: [{ vid: '11', name: '红色' }] },
        { pid: '2', name: '尺码', values: [{ vid: '21', name: 'M' }] },
      ],
      skus: [{ skuId: 'sku-red-m', propPath: '1:11;2:21' }],
    },
    skuCore: { sku2info: { 'sku-red-m': { price: '65', quantity: 9 } } },
  });

  assert.ok(captured);
  assert.equal(captured.shape, 'skuBase.props+skuBase.skus');
  assert.deepEqual(captured.combinations[0].options, { 颜色: '红色', 尺码: 'M' });
  assert.equal(captured.combinations[0].stock, 9);
});

test('discovers renamed page state globals with SKU-related names', () => {
  const documentRef = { querySelectorAll: () => [] };
  const globalRef = {
    __OFFER_DETAIL_STATE__: {
      skuProps: [{ propId: '1', propName: '规格', values: [{ valueId: '2', valueName: '36键' }] }],
      skuInfoMap: { '1:2': { offerSkuId: 'keyboard-36', stock: 3 } },
    },
  };

  const captured = capture(documentRef, globalRef);

  assert.equal(captured.combinations.length, 1);
  assert.equal(captured.combinations[0].skuId, 'keyboard-36');
});

test('preserves category-specific and ambiguous-looking option texts as observed evidence', () => {
  const captured = extractFromObject({
    skuBase: {
      props: [
        { pid: '1', name: '颜色分类', values: [{ vid: '11', name: '9号' }] },
        { pid: '2', name: '规格', values: [{ vid: '21', name: '均码' }] },
        { pid: '3', name: '键位', values: [{ vid: '31', name: '36键' }] },
      ],
    },
    skuCore: {
      sku2info: {
        '1:11;2:21;3:31': { skuId: 'sku-observed', price: '42', quantity: 0 },
      },
    },
  });

  assert.ok(captured);
  assert.equal(captured.combinations.length, 1);
  assert.deepEqual(captured.combinations[0].options, {
    颜色分类: '9号',
    规格: '均码',
    键位: '36键',
  });
  assert.equal(captured.combinations[0].stock, 0);
  assert.equal(captured.combinations[0].availability, 'unavailable');
});

test('parses static JSON assignments without executing page code', () => {
  globalThis.__AUTO_MAGIC_PROBE_EXECUTED__ = false;
  const values = parseStaticJsonValues(
    'window.__INIT_DATA__ = {"skuProps":[]}; globalThis.__AUTO_MAGIC_PROBE_EXECUTED__ = true;',
  );

  assert.deepEqual(values, [{ skuProps: [] }]);
  assert.equal(globalThis.__AUTO_MAGIC_PROBE_EXECUTED__, false);
  delete globalThis.__AUTO_MAGIC_PROBE_EXECUTED__;
});

test('parses JSON.parse encoded state without evaluating script code', () => {
  const state = JSON.stringify({
    skuProps: [{ propId: '1', propName: '颜色', values: [{ valueId: '2', valueName: '红色' }] }],
    skuInfoMap: { '1:2': { skuId: 'red' } },
  });
  const values = parseStaticJsonValues(`window.__STATE__ = JSON.parse(${JSON.stringify(state)});`);

  assert.equal(values.length, 1);
  assert.equal(values[0].skuInfoMap['1:2'].skuId, 'red');
});
