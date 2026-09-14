/**
 * Readable extraction of the 1688 source-product collector in maozi-plugin 3.2.6.
 *
 * This module deliberately has no API call, Ozon mapping, authentication, or UI.
 * Run collect1688SourceProduct() in a rendered detail.1688.com offer page and send
 * the returned object to whichever persistence layer owns the collection task.
 */

export function collect1688SourceProduct({
  documentRef = document,
  locationRef = location,
  now = () => Date.now(),
} = {}) {
  const product = collectProductSummary(documentRef);
  const specificationGroups = collectSpecificationGroups(documentRef);
  const skuDetails = collectSkuDetails(documentRef);
  const attributes = collectAttributes(documentRef);
  const description = collectDescription(documentRef);

  return {
    origin_data: {
      collected_by: 'dom_parser',
      timestamp: now(),
    },
    data: {
      url: locationRef.href,
      goods_id: locationRef.pathname.split('/').pop()?.replace(/\.html$/, '') || '',
      goods_name: product.title,
      description,
      images: product.images,
      video: product.video,
      skus: buildSkus({ product, specificationGroups, skuDetails, documentRef }),
      attributes,
      productPackInfo: { weight: collectWeight(documentRef) },
    },
    collect_from: '1688',
  };
}

export function collectProductSummary(documentRef) {
  const title = text(documentRef.querySelector('.title-text'))
    || text(documentRef.querySelector('.title-content'));

  const imageUrls = [];
  const primarySelectors = [
    '.gallery-img img',
    '.main-image img',
    '.detail-gallery-img img',
    '.thumb-img img',
    '.thumbnail img',
    '.detail-gallery img',
    '.preview-list img',
    '.fd-clr img',
    '.od-pc-offer-tab img',
    '.offer-detail-tab img',
    '.main-pic img',
    '.pic-view img',
    '.preview-wrap img',
    '.detail-pic img',
    '.product-image img',
    '.offer-image img',
  ];

  for (const selector of primarySelectors) {
    for (const image of documentRef.querySelectorAll(selector)) {
      const url = image.src || image.getAttribute('data-src') || image.getAttribute('data-lazy');
      if (isProductImage(image, url)) imageUrls.push(normalize1688Image(url));
    }
  }

  // The original collector falls back to all product-CDN images when known galleries fail.
  if (imageUrls.length === 0) {
    for (const image of documentRef.querySelectorAll('img')) {
      const url = image.src;
      const fromProductCdn = /1688|alibaba|alicdn/.test(url || '');
      if (fromProductCdn && isProductImage(image, url)) imageUrls.push(normalize1688Image(url));
    }
  }

  const videoSource = documentRef.querySelector('video source');
  const video = videoSource?.src || documentRef.querySelector('video')?.src || '';
  return { title, images: unique(imageUrls), video };
}

export function collectSpecificationGroups(documentRef) {
  const values = [];
  const modern = documentRef.querySelectorAll('.prop-item-inner-wrapper');
  const legacy = documentRef.querySelectorAll('.sku-filter-button');
  const alternate = documentRef.querySelectorAll('.sku-props-list .selector-prop-item');

  if (modern.length) {
    for (const item of modern) {
      const name = text(item.querySelector('.prop-name'));
      const image = backgroundImageUrl(item.querySelector('.prop-img'));
      if (name) values.push({ name, image });
    }
  } else if (legacy.length) {
    for (const item of legacy) {
      const name = text(item.querySelector('.label-name'));
      const image = normalize1688Image(item.querySelector('img')?.src || '');
      if (name) values.push({ name, image });
    }
  } else if (alternate.length) {
    for (const item of alternate) {
      const name = text(item.querySelector('.prop-item-text'));
      const image = normalize1688Image(item.querySelector('img')?.src || '');
      if (name) values.push({ name, image });
    }
  }

  return values.length ? [{ name: '规格选项', values }] : [];
}

export function collectSkuDetails(documentRef) {
  const readers = [
    () => readSkuItems(documentRef.querySelectorAll('.sku-item-wrapper'), {
      image: (row) => backgroundImageUrl(row.querySelector('.sku-item-image'))
        || normalize1688Image(row.querySelector('.sku-item-img img, .sku-wrapper-img img, img')?.src || ''),
      name: (row, index) => text(row.querySelector('.sku-item-name')) || `规格${index + 1}`,
      price: (row) => numberFromText(text(row.querySelector('.discountPrice-price'))),
    }),
    () => readSkuItems(documentRef.querySelectorAll('.expand-view-item'), {
      image: (row) => normalize1688Image(row.querySelector('img')?.src || row.querySelector('img')?.getAttribute('data-src') || ''),
      name: (row, index) => text(row.querySelector('.item-label')) || `规格${index + 1}`,
      price: (row) => numberFromText(text(row.querySelector('.item-price-stock'))),
    }),
    () => readSkuItems(documentRef.querySelectorAll('.sku-list-item'), {
      image: (row) => normalize1688Image(row.querySelector('img')?.src || ''),
      name: (row, index) => text(row.querySelector('.sku-item-name-text')) || `规格${index + 1}`,
      price: (row) => numberFromText(text(row.querySelector('.sku-item-price'))),
    }),
    () => readSingleSkuWrappers(documentRef),
    () => readTableSkus(documentRef),
  ];

  for (const read of readers) {
    const skus = read();
    if (skus.length) return skus;
  }
  return [];
}

export function collectAttributes(documentRef) {
  const offerItems = [...documentRef.querySelectorAll('.offer-attr-item')]
    .map((row) => ({ name: text(row.querySelector('.offer-attr-item-name')), value: text(row.querySelector('.offer-attr-item-value')) }))
    .filter(hasNameAndValue);
  if (offerItems.length) return offerItems;

  const descriptionRows = [...documentRef.querySelectorAll('#productAttributes .ant-descriptions-row, .ant-descriptions-row')];
  const descriptionItems = [];
  for (const row of descriptionRows) {
    const labels = row.querySelectorAll('.ant-descriptions-item-label span');
    const values = row.querySelectorAll('.ant-descriptions-item-content .field-value');
    for (let index = 0; index < Math.min(labels.length, values.length); index += 1) {
      const item = { name: text(labels[index]), value: text(values[index]) };
      if (hasNameAndValue(item)) descriptionItems.push(item);
    }
  }
  if (descriptionItems.length) return descriptionItems;

  const standardItems = [...documentRef.querySelectorAll('.attr-item, .product-attr-item')]
    .map((row) => ({ name: text(row.querySelector('.attr-name, .attr-key')), value: text(row.querySelector('.attr-value')) }))
    .filter(hasNameAndValue);
  if (standardItems.length) return standardItems;

  return [...documentRef.querySelectorAll('.attr-table tr, .product-params tr')]
    .map((row) => {
      const cells = row.querySelectorAll('td, th');
      return { name: text(cells[0]), value: text(cells[1]) };
    })
    .filter(({ name, value }) => name && value && !name.includes('属性') && !name.includes('参数'));
}

export function collectDescription(documentRef) {
  const texts = [];
  const images = [];
  for (const selector of ['#detailContentContainer', '.html-description']) {
    for (const node of documentRef.querySelectorAll(selector)) {
      addLongText(texts, text(node));
      const shadow = node.shadowRoot;
      if (shadow) {
        addLongText(texts, text(shadow.querySelector('.rich-text-component')));
        for (const descendant of shadow.querySelectorAll('div, p, span')) addLongText(texts, text(descendant));
      }
    }

    const root = documentRef.querySelector(selector);
    const imageNodes = [
      ...documentRef.querySelectorAll(`${selector} img`),
      ...(root?.shadowRoot ? [...root.shadowRoot.querySelectorAll('img')] : []),
    ];
    for (const image of imageNodes) {
      const url = detailImageUrl(image);
      if (url) images.push(url);
    }
  }
  return { text: unique(texts).join('\n'), images: unique(images) };
}

export function buildSkus({ product, specificationGroups, skuDetails, documentRef }) {
  const options = specificationGroups.flatMap((group) => group.values.map((value) => ({ name: value.name, image: value.image || '' })));

  // This intentionally mirrors the original plugin's simple option × row expansion.
  if (options.length && skuDetails.length) {
    return options.flatMap((option) => skuDetails.map((sku) => ({
      name: `${option.name}-${sku.name}`,
      price: sku.price,
      primary_image: option.image || sku.image || product.images[0] || '',
      video: product.video,
    })));
  }
  if (skuDetails.length) {
    return skuDetails.map((sku) => ({
      name: sku.name,
      price: sku.price,
      primary_image: sku.image || product.images[0] || '',
      video: product.video,
    }));
  }
  if (options.length) {
    const price = numberFromText(text(documentRef.querySelector('.price, .current-price, .item-price-stock')));
    return options.map((option) => ({ name: option.name, price, primary_image: option.image || product.images[0] || '', video: product.video }));
  }
  return [{
    name: product.title || '未命名',
    price: numberFromText(text(documentRef.querySelector('.price, .current-price, .item-price-stock, .discountPrice-price'))),
    primary_image: product.images[0] || '',
    video: product.video,
  }];
}

function readSkuItems(rows, { image, name, price }) {
  return [...rows].map((row, index) => ({ name: name(row, index), price: price(row), image: image(row) || '' })).filter((sku) => sku.name);
}

function readSingleSkuWrappers(documentRef) {
  const priceRows = documentRef.querySelectorAll('.single-price-warp');
  return [...documentRef.querySelectorAll('.single-sku-list-wrap')].map((row, index) => ({
    name: text(row.querySelectorAll('.single-sku-title span')[1]) || `规格${index + 1}`,
    price: numberFromText(text(priceRows[index]?.querySelector('.price-title'))),
    image: backgroundImageUrl(row.querySelector('.single-sku-img-pop')) || '',
  }));
}

function readTableSkus(documentRef) {
  const table = documentRef.querySelector('.next-table-body table');
  if (!table) return [];
  return [...table.querySelectorAll('tr')].map((row, index) => ({
    name: text(row.querySelector('span.normal-text')) || `规格${index + 1}`,
    price: numberFromText(text(row.querySelector('.price'))),
    image: backgroundImageUrl(row.querySelector('.od-gyp-pc-sku-selection-sku')) || '',
  })).filter((sku) => sku.name);
}

function collectWeight(documentRef) {
  return numberFromText(text(documentRef.querySelector('#productPackInfo td.field-value')));
}

function detailImageUrl(image) {
  const candidates = [image.src, image.getAttribute('data-lazyload-src'), image.getAttribute('data-src'), image.getAttribute('data-original'), image.getAttribute('data-lazy')];
  return candidates.find((url) => url && url.startsWith('http') && !/data:image|placeholder|loading|lazyload\.png/.test(url)) || '';
}

function backgroundImageUrl(element) {
  if (!element) return '';
  const value = globalThis.getComputedStyle?.(element)?.backgroundImage || '';
  const match = value.match(/http.*?\.(?:jpg|png|jpeg)/i);
  return match ? normalize1688Image(match[0]) : '';
}

function normalize1688Image(url) {
  if (!url) return '';
  return url.replace('.jpg_sum.jpg', '.jpg').replace('.jpg_b.jpg', '.jpg');
}

function isProductImage(image, url) {
  return Boolean(url) && !/data:image|placeholder|arrow|button/.test(url)
    && !image.closest('button, .button, [role="button"], .od-gallery-button, .gallery-button, .video-icon, .btn')
    && image.width > 20 && image.height > 20;
}

function addLongText(items, value) {
  if (value && value.length > 10 && !items.includes(value)) items.push(value);
}

function text(node) {
  return node?.textContent?.trim() || '';
}

function numberFromText(value) {
  return Number.parseFloat((value || '0').replace(/[^\d.]/g, '')) || 0;
}

function hasNameAndValue(item) {
  return Boolean(item.name && item.value);
}

function unique(items) {
  return [...new Set(items.filter(Boolean))];
}
