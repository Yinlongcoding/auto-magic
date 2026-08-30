import assert from 'node:assert/strict';
import { readFile } from 'node:fs/promises';
import test from 'node:test';

const snapshotSource = await readFile(
  new URL('../plugin/lib/dom-snapshot.js', import.meta.url),
  'utf8',
);
const { serializeDomTree } = await import(
  `data:text/javascript;base64,${Buffer.from(snapshotSource).toString('base64')}`
);

function createElement(tagName, { attributes = {}, text = '', children = [] } = {}) {
  return {
    tagName: tagName.toUpperCase(),
    attributes: Object.entries(attributes).map(([name, value]) => ({ name, value })),
    childNodes: text ? [{ nodeType: 3, textContent: text }] : [],
    children,
  };
}

test('serializes a safe, readable detail DOM tree', () => {
  const root = createElement('html', {
    children: [
      createElement('body', {
        attributes: { class: 'offer-detail', onclick: 'alert(1)' },
        children: [
          createElement('h1', {
            attributes: { class: 'title', 'data-offer-id': '123' },
            text: '  连衣裙   商品标题  ',
          }),
          createElement('script', { text: 'alert(1)' }),
        ],
      }),
    ],
  });

  const result = serializeDomTree(root);

  assert.equal(result.nodeCount, 3);
  assert.equal(result.truncated, false);
  assert.equal(result.tree.children[0].attributes.class, 'offer-detail');
  assert.equal('onclick' in result.tree.children[0].attributes, false);
  assert.equal(result.tree.children[0].children[0].text, '连衣裙 商品标题');
  assert.equal(result.tree.children[0].children.length, 1);
});

test('enforces node and depth limits for large detail pages', () => {
  const root = createElement('html', {
    children: [
      createElement('body', {
        children: Array.from({ length: 10 }, (_, index) =>
          createElement('div', { text: `节点${index + 1}` }),
        ),
      }),
    ],
  });

  const result = serializeDomTree(root, { maxNodes: 4, maxDepth: 10 });

  assert.equal(result.nodeCount, 4);
  assert.equal(result.truncated, true);
  assert.equal(result.tree.children[0].children.length, 2);
  assert.equal(result.tree.children[0].childrenTruncated, 8);
});
