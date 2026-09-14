import assert from 'node:assert/strict';
import { readFile } from 'node:fs/promises';
import test from 'node:test';
import vm from 'node:vm';

const extractorSource = await readFile(
  new URL('../plugin/content/extractor.js', import.meta.url),
  'utf8',
);

const selectors = {
  productCard:
    '.search-offer-item',
  productLink: ':scope > a[href], a[href]',
  productImage: '.main-img',
  productTitle: '.title-text',
  productPrice: '.text-main',
};

function createCard({
  detailUrl,
  imageUrl,
  imageAttributes = {},
  titleText,
  titleHtml,
  priceText,
}) {
  const elements = {
    [selectors.productLink]: {
      href: detailUrl,
      getAttribute: (name) => (name === 'href' ? detailUrl : null),
    },
    [selectors.productImage]: {
      currentSrc: imageUrl,
      getAttribute: (name) => imageAttributes[name] ?? null,
    },
    [selectors.productTitle]: {
      textContent: titleText,
      innerHTML: titleHtml,
    },
    [selectors.productPrice]: { textContent: priceText },
  };

  return {
    matches: () => false,
    querySelector: (selector) => elements[selector] ?? null,
    querySelectorAll: (selector) =>
      selector === 'a[href]' && elements[selectors.productLink]
        ? [elements[selectors.productLink]]
        : [],
  };
}

function runExtraction(cards, message = {}) {
  let messageListener;
  let response;
  const context = {
    URL,
    chrome: {
      runtime: {
        onMessage: {
          addListener(listener) {
            messageListener = listener;
          },
        },
      },
    },
    document: {
      baseURI: 'https://s.1688.com/selloffer/offer_search.htm',
      title: '1688 搜索结果',
      querySelectorAll: (selector) => (selector === selectors.productCard ? cards : []),
    },
    location: { href: 'https://s.1688.com/selloffer/offer_search.htm' },
  };

  vm.runInNewContext(extractorSource, context);
  messageListener(
    {
      type: 'EXTRACT_1688_PRODUCTS',
      maxItems: 60,
      selectors,
      ...message,
    },
    {},
    (value) => {
      response = value;
    },
  );
  return response;
}

test('extracts link, image, pure-text title and price from the new card structure', () => {
  const card = createCard({
    detailUrl: 'https://detail.1688.com/offer/123456.html',
    imageUrl: 'data:image/gif;base64,placeholder',
    imageAttributes: {
      'data-src': 'https://cbu01.alicdn.com/img/ibank/example.jpg',
    },
    titleText: '“2025欧美热销性感蕾丝深V透视鱼尾长裙速卖通INS博主同款” 连衣裙 “女”',
    titleHtml:
      '“2025欧美热销性感蕾丝深V透视鱼尾长裙速卖通INS博主同款”<font color="red">连衣裙</font>“女”',
    priceText: '43.80',
  });

  const response = runExtraction([card]);

  assert.equal(response.success, true);
  assert.equal(response.data.count, 1);
  const { cardIndex: _cardIndex, ...firstItem } = response.data.items[0];
  assert.deepEqual(
    firstItem,
    {
      detailUrl: 'https://detail.1688.com/offer/123456.html',
      imageUrl: 'https://cbu01.alicdn.com/img/ibank/example.jpg',
      title: '“2025欧美热销性感蕾丝深V透视鱼尾长裙速卖通INS博主同款” 连衣裙 “女”',
      priceCny: '43.80',
    },
  );
  assert.equal(response.data.items[0].title.includes('<font'), false);
  assert.deepEqual(JSON.parse(JSON.stringify(response.data.diagnostics)), { rawCardCount: 1, outputCount: 1, unresolvedDetailCards: [] });
});

test('keeps all 60 cards when every product has a unique valid detail link', () => {
  const cards = Array.from({ length: 60 }, (_, index) =>
    createCard({
      detailUrl: `https://detail.1688.com/offer/${index + 1}.html`,
      imageUrl: `https://cbu01.alicdn.com/item/${index + 1}.jpg`,
      titleText: `连衣裙${index + 1}`,
      priceText: '19.90',
    }),
  );

  const response = runExtraction(cards);

  assert.equal(response.success, true);
  assert.equal(response.data.diagnostics.rawCardCount, 60);
  assert.equal(response.data.diagnostics.outputCount, 60);
  assert.equal(response.data.count, 60);
  assert.equal(response.data.items.length, 60);
});

test('upgrades HTTP offer links and keeps non-offer 1688 cards for dynamic resolution', () => {
  const cards = Array.from({ length: 60 }, (_, index) =>
    createCard({
      detailUrl:
        index < 49
          ? `http://detail.m.1688.com/page/index.html?offerId=${index + 1}`
          : `https://dj.1688.com/ci_king?idx=${index + 1}`,
      imageUrl: `https://cbu01.alicdn.com/item/${index + 1}.jpg`,
      titleText: `连衣裙${index + 1}`,
      priceText: '19.90',
    }),
  );

  const response = runExtraction(cards);

  assert.equal(response.success, true);
  assert.equal(response.data.count, 60);
  assert.equal(response.data.diagnostics.rawCardCount, 60);
  assert.equal(response.data.diagnostics.unresolvedDetailCards.length, 11);
  assert.equal(response.data.items[0].detailUrl.startsWith('https://'), true);
  assert.equal(response.data.items[59].detailUrl, null);
});

test('preserves duplicate and unresolved list records', () => {
  const validCard = createCard({
    detailUrl: 'https://detail.1688.com/offer/123456.html',
    imageUrl: 'http://example.com/insecure.jpg',
    titleText: '商品A',
    titleHtml: '商品A',
    priceText: '9.90',
  });
  const duplicateCard = createCard({
    detailUrl: 'https://detail.1688.com/offer/123456.html',
    imageUrl: 'https://cbu01.alicdn.com/duplicate.jpg',
    titleText: '重复商品',
    titleHtml: '重复商品',
    priceText: '10.00',
  });
  const unsafeCard = createCard({
    detailUrl: 'javascript:alert(1)',
    imageUrl: 'https://cbu01.alicdn.com/unsafe.jpg',
    titleText: '不安全商品',
    titleHtml: '不安全商品',
    priceText: '1.00',
  });

  const response = runExtraction([validCard, duplicateCard, unsafeCard]);

  assert.equal(response.success, true);
  assert.equal(response.data.count, 3);
  assert.equal(response.data.items[0].imageUrl, null);
  assert.equal(response.data.diagnostics.rawCardCount, 3);
  assert.equal(response.data.items[1].detailUrl, 'https://detail.1688.com/offer/123456.html');
  assert.equal(response.data.items[2].detailUrl, null);
});

test('reports cards rejected for missing, malformed and non-1688 links', () => {
  const missingCard = createCard({
    detailUrl: null,
    imageUrl: null,
    titleText: '无链接商品',
    priceText: '1.00',
  });
  const malformedCard = createCard({
    detailUrl: 'https://[invalid',
    imageUrl: null,
    titleText: '错误链接商品',
    priceText: '2.00',
  });
  const externalCard = createCard({
    detailUrl: 'https://example.com/product/1',
    imageUrl: null,
    titleText: '外部链接商品',
    priceText: '3.00',
  });

  const response = runExtraction([missingCard, malformedCard, externalCard]);

  assert.equal(response.data.count, 3);
  assert.equal(response.data.diagnostics.rawCardCount, 3);
  assert.equal(response.data.items.length, 3);
});

test('does not mislabel a 1688 shop homepage as a product detail URL', () => {
  const response = runExtraction([createCard({
    detailUrl: 'http://shop4814e04746556.1688.com/',
    imageUrl: 'https://cbu01.alicdn.com/item/ad.jpg',
    titleText: '广告商品',
    priceText: '55',
  })]);

  assert.equal(response.data.count, 1);
  assert.equal(response.data.items[0].detailUrl, null);
  assert.equal(response.data.diagnostics.unresolvedDetailCards[0].cardIndex, 0);
});
