(() => {
  if (globalThis.__AUTO_MAGIC_STRUCTURED_SKU_READER__) return;

  const MAX_ROOTS = 60;
  const MAX_VISITED_NODES = 20_000;
  const MAX_DEPTH = 14;
  const GLOBAL_ROOT_NAMES = [
    '__INIT_DATA__',
    '__INITIAL_STATE__',
    '__GLOBAL_DATA__',
    '__NEXT_DATA__',
    '__APOLLO_STATE__',
    'rawData',
    'detailData',
    'offerDetailData',
  ];

  function capture(documentRef = document, globalRef = globalThis) {
    const roots = collectRoots(documentRef, globalRef);
    const candidates = [];
    let visitedNodeCount = 0;

    for (const root of roots.slice(0, MAX_ROOTS)) {
      const result = scanRoot(root.value, root.path, MAX_VISITED_NODES - visitedNodeCount);
      visitedNodeCount += result.visitedNodeCount;
      candidates.push(...result.candidates);
      if (visitedNodeCount >= MAX_VISITED_NODES) break;
    }

    const uniqueCandidates = candidates
      .filter((candidate, index, items) => items.findIndex((item) =>
        item.signature === candidate.signature) === index)
      .sort((left, right) => score(right) - score(left));
    const selected = uniqueCandidates[0] ?? null;
    if (!selected) {
      return {
        status: 'not_found',
        dimensions: [],
        combinations: [],
        diagnostics: {
          strategy: 'page-context-structured-data',
          inspectedRootCount: roots.length,
          inspectedRootPaths: roots.slice(0, 20).map((root) => root.path),
          visitedNodeCount,
          candidateCount: 0,
          selectedSourcePath: null,
        },
      };
    }

    return {
      status: selected.combinations.length > 0 ? 'verified' : 'dimensions_only',
      dimensions: selected.dimensions,
      combinations: selected.combinations,
      diagnostics: {
        strategy: 'page-context-structured-data',
        inspectedRootCount: roots.length,
        inspectedRootPaths: roots.slice(0, 20).map((root) => root.path),
        visitedNodeCount,
        candidateCount: uniqueCandidates.length,
        selectedSourcePath: selected.sourcePath,
        selectedShape: selected.shape,
        dimensionCount: selected.dimensions.length,
        combinationCount: selected.combinations.length,
      },
    };
  }

  function extractFromObject(value, sourcePath = '$') {
    const result = scanRoot(value, sourcePath, MAX_VISITED_NODES);
    return result.candidates.sort((left, right) => score(right) - score(left))[0] ?? null;
  }

  function collectRoots(documentRef, globalRef) {
    const roots = [];
    const seenObjects = new WeakSet();
    const addRoot = (path, value) => {
      if (!value || typeof value !== 'object' || seenObjects.has(value) || roots.length >= MAX_ROOTS) return;
      seenObjects.add(value);
      roots.push({ path, value });
    };
    for (const name of GLOBAL_ROOT_NAMES) {
      try {
        const value = globalRef?.[name];
        addRoot(`window.${name}`, value);
      } catch {
        // 页面属性可能是会抛错的 getter；跳过该候选即可。
      }
    }

    // 1688会按页面版本改变全局状态变量名。这里只读取名称明确与商品/SKU相关的对象，
    // 不执行页面代码，也不遍历无关的浏览器全局对象。
    for (const name of safePropertyNames(globalRef)) {
      if (!/(?:sku|offer|detail|initial|product).*(?:data|state|model)?|(?:data|state|model).*(?:sku|offer|detail|product)/iu.test(name)) continue;
      try { addRoot(`window.${name}`, globalRef[name]); } catch { /* 跳过会抛错的getter。 */ }
    }

    const skuElements = Array.from(documentRef?.querySelectorAll?.(
      '[class*="sku"], [class*="Sku"], [class*="spec"], [class*="Spec"], [data-sku], [data-props]',
    ) ?? []).slice(0, 80);
    for (const [index, element] of skuElements.entries()) {
      for (const name of safePropertyNames(element)) {
        if (!/^__react(?:Props|Fiber)\$.+/u.test(name)) continue;
        try { addRoot(`document.skuElements[${index}].${name}`, element[name]); } catch { /* 忽略。 */ }
      }
      for (const attribute of Array.from(element?.attributes ?? [])) {
        if (!/(?:sku|spec|prop|data)/iu.test(attribute.name) || !/[{[]/u.test(attribute.value)) continue;
        for (const value of parseStaticJsonValues(attribute.value))
          addRoot(`document.skuElements[${index}].${attribute.name}`, value);
      }
    }

    const scripts = Array.from(documentRef?.querySelectorAll?.(
      'script[type="application/json"], script[type="application/ld+json"], script:not([src])',
    ) ?? []).slice(0, 40);
    for (const [index, script] of scripts.entries()) {
      const text = String(script.textContent ?? '').trim();
      if (!text || text.length > 4_000_000 || !/sku|规格|spec/i.test(text)) continue;
      for (const value of parseStaticJsonValues(text)) {
        addRoot(`document.scripts[${index}]`, value);
        if (roots.length >= MAX_ROOTS) break;
      }
      if (roots.length >= MAX_ROOTS) break;
    }
    return roots;
  }

  function scanRoot(root, rootPath, nodeBudget) {
    const queue = [{ value: root, path: rootPath, depth: 0 }];
    const seen = new WeakSet();
    const candidates = [];
    let visitedNodeCount = 0;

    while (queue.length > 0 && visitedNodeCount < nodeBudget) {
      const current = queue.shift();
      if (!current?.value || typeof current.value !== 'object') continue;
      if (seen.has(current.value)) continue;
      seen.add(current.value);
      visitedNodeCount += 1;

      const parsed = parseCandidate(current.value, current.path);
      if (parsed) candidates.push(parsed);
      if (current.depth >= MAX_DEPTH) continue;

      for (const [key, child] of safeEntries(current.value)) {
        if (!child || typeof child !== 'object') continue;
        queue.push({
          value: child,
          path: appendPath(current.path, key),
          depth: current.depth + 1,
        });
      }
    }
    return { candidates, visitedNodeCount };
  }

  function parseCandidate(value, sourcePath) {
    const paired = value.skuBase?.props && value.skuCore?.sku2info
      ? {
          props: value.skuBase.props,
          info: value.skuCore.sku2info,
          infoPath: `${sourcePath}.skuCore.sku2info`,
          shape: 'skuBase+skuCore',
        }
      : null;
    const joined = value.skuBase?.props && Array.isArray(value.skuBase?.skus)
      ? {
          props: value.skuBase.props,
          info: value.skuBase.skus.map((sku) => ({
            ...(findSkuInfo(value.skuCore?.sku2info, sku) ?? {}),
            ...sku,
          })),
          infoPath: `${sourcePath}.skuBase.skus`,
          shape: 'skuBase.props+skuBase.skus',
        }
      : null;
    const directProps = value.skuProps ?? value.props ?? value.specifications ?? value.specProps;
    const directInfo = value.skuInfoMap ?? value.sku2info ?? value.skuMap ?? value.skuInfos;
    const direct = Array.isArray(directProps) && directInfo && typeof directInfo === 'object'
      ? {
          props: directProps,
          info: directInfo,
          infoPath: sourcePath,
          shape: 'skuProps+skuInfoMap',
        }
      : null;
    const source = joined ?? paired ?? direct;
    if (!source) return null;

    const parsedDimensions = parseDimensions(source.props, sourcePath);
    if (parsedDimensions.dimensions.length === 0) return null;
    const combinations = parseCombinations(
      source.info,
      source.infoPath,
      parsedDimensions.dimensions,
      parsedDimensions.optionLookup,
    );
    const signature = JSON.stringify({
      d: parsedDimensions.dimensions.map((dimension) => [
        dimension.name,
        dimension.options.map((option) => option.sourceOptionId ?? option.sourceValue),
      ]),
      c: combinations.map((combination) => combination.combinationKey),
    });
    return {
      sourcePath,
      shape: source.shape,
      dimensions: parsedDimensions.dimensions,
      combinations,
      signature,
    };
  }

  function parseDimensions(props, sourcePath) {
    const dimensions = [];
    const optionLookup = new Map();
    for (const [dimensionIndex, prop] of Array.from(props ?? []).entries()) {
      if (!prop || typeof prop !== 'object') continue;
      const name = firstText(prop.name, prop.prop, prop.label, prop.title, prop.propName);
      const dimensionId = firstText(prop.pid, prop.id, prop.propId, prop.propertyId);
      const values = prop.values ?? prop.value ?? prop.options ?? prop.children;
      if (!name || !Array.isArray(values)) continue;

      const options = [];
      for (const [optionIndex, option] of values.entries()) {
        const item = option && typeof option === 'object' ? option : { value: option };
        const sourceValue = firstText(
          item.name, item.value, item.text, item.label, item.displayName, item.valueName,
        );
        if (!sourceValue) continue;
        const sourceOptionId = firstText(
          item.vid, item.id, item.valueId, item.propValueId, item.propertyValueId,
        );
        const optionKey = sourceOptionId && dimensionId
          ? `${dimensionId}:${sourceOptionId}`
          : sourceOptionId || `${dimensionIndex + 1}:${optionIndex + 1}`;
        const parsed = {
          optionKey,
          sourceOptionId: sourceOptionId || null,
          sourceValue,
          normalizedValue: sourceValue,
          status: 'captured',
          imageUrl: firstUrl(item.imageUrl, item.image, item.picUrl, item.pic, item.avatar),
        };
        options.push(parsed);
        for (const key of [
          optionKey,
          sourceOptionId,
          firstText(item.path, item.propPath, item.specId),
        ].filter(Boolean)) {
          optionLookup.set(String(key), { dimensionName: name, option: parsed });
        }
      }
      if (options.length > 0) {
        dimensions.push({
          name,
          source: `structured-json:${sourcePath}`,
          sourceDimensionId: dimensionId || null,
          options,
        });
      }
    }
    return { dimensions, optionLookup };
  }

  function parseCombinations(infoMap, sourcePath, dimensions, optionLookup) {
    const entries = Array.isArray(infoMap)
      ? infoMap.map((value, index) => [String(index), value])
      : safeEntries(infoMap);
    const combinations = [];
    for (const [mapKey, rawInfo] of entries) {
      if (mapKey === '$default' || !rawInfo || typeof rawInfo !== 'object') continue;
      const propPath = firstText(
        rawInfo.propPath,
        rawInfo.skuPropPath,
        rawInfo.specId,
        rawInfo.specification,
        mapKey,
      );
      const decoded = decodeOptions(propPath, dimensions, optionLookup);
      if (Object.keys(decoded.options).length === 0) continue;

      const skuId = firstText(rawInfo.skuId, rawInfo.id, rawInfo.skuIdStr, rawInfo.offerSkuId);
      const stock = firstInteger(
        rawInfo.stock,
        rawInfo.quantity,
        rawInfo.canBookCount,
        rawInfo.amountOnSale,
        rawInfo.inventory,
      );
      const price = firstNumber(
        rawInfo.price,
        rawInfo.promotionPrice,
        rawInfo.discountPrice,
        rawInfo.salePrice,
        rawInfo.priceValue,
        rawInfo.priceInfo?.price,
      );
      const availability = stock === null ? 'unknown' : stock > 0 ? 'available' : 'unavailable';
      combinations.push({
        skuId: skuId || null,
        combinationKey: skuId ? `sku:${skuId}` : `path:${propPath}`,
        verification: 'structured-json',
        options: decoded.options,
        optionIds: decoded.optionIds,
        price,
        stock,
        availability,
        imageUrl: firstUrl(
          rawInfo.imageUrl, rawInfo.image, rawInfo.picUrl, rawInfo.pic, rawInfo.skuImage,
        ),
        sourcePath: appendPath(sourcePath, mapKey),
      });
    }
    return combinations.filter((combination, index, items) =>
      items.findIndex((item) => item.combinationKey === combination.combinationKey) === index);
  }

  function decodeOptions(propPath, dimensions, optionLookup) {
    const options = {};
    const optionIds = {};
    const path = String(propPath ?? '').trim();
    const tokens = path.split(/[;|,]/u).map((item) => item.trim()).filter(Boolean);
    for (const token of tokens) {
      const direct = optionLookup.get(token);
      const pair = direct ?? (() => {
        const parts = token.split(':');
        return optionLookup.get(parts.at(-1)) ?? null;
      })();
      if (!pair) continue;
      options[pair.dimensionName] = pair.option.sourceValue;
      optionIds[pair.dimensionName] = pair.option.sourceOptionId ?? pair.option.optionKey;
    }

    if (Object.keys(options).length === 0 && dimensions.length === 1) {
      const pair = optionLookup.get(path);
      if (pair) {
        options[pair.dimensionName] = pair.option.sourceValue;
        optionIds[pair.dimensionName] = pair.option.sourceOptionId ?? pair.option.optionKey;
      }
    }
    return { options, optionIds };
  }

  function parseStaticJsonValues(text) {
    const values = [];
    try {
      const parsed = JSON.parse(text);
      if (parsed && typeof parsed === 'object') values.push(parsed);
      return values;
    } catch {
      // 继续解析 window.__DATA__ = {...} 一类静态JSON赋值；绝不执行页面文本。
    }

    const jsonParsePattern = /JSON\.parse\(\s*("(?:\\.|[^"\\])*")\s*\)/gu;
    for (const match of text.matchAll(jsonParsePattern)) {
      if (values.length >= 10) break;
      try {
        const encoded = JSON.parse(match[1]);
        const parsed = JSON.parse(encoded);
        if (parsed && typeof parsed === 'object') values.push(parsed);
      } catch {
        // 只接受标准JSON字符串和其中的标准JSON内容。
      }
    }

    const assignmentPattern = /=\s*([{[])/g;
    for (const match of text.matchAll(assignmentPattern)) {
      if (values.length >= 10) break;
      const start = Number(match.index) + match[0].lastIndexOf(match[1]);
      const jsonText = readBalancedJson(text, start);
      if (!jsonText) continue;
      try {
        const parsed = JSON.parse(jsonText);
        if (parsed && typeof parsed === 'object') values.push(parsed);
      } catch {
        // JavaScript对象字面量不是JSON，不使用eval放宽解析。
      }
    }
    return values;
  }

  function readBalancedJson(source, start) {
    const opening = source[start];
    if (opening !== '{' && opening !== '[') return null;
    const closing = opening === '{' ? '}' : ']';
    let depth = 0;
    let quote = null;
    let escaped = false;
    for (let index = start; index < source.length; index += 1) {
      const character = source[index];
      if (quote) {
        if (escaped) escaped = false;
        else if (character === '\\') escaped = true;
        else if (character === quote) quote = null;
        continue;
      }
      if (character === '"') quote = character;
      else if (character === opening) depth += 1;
      else if (character === closing && --depth === 0) return source.slice(start, index + 1);
    }
    return null;
  }

  function score(candidate) {
    return candidate.combinations.length * 100 +
      candidate.dimensions.reduce((count, dimension) => count + dimension.options.length, 0);
  }

  function safeEntries(value) {
    try {
      return Object.entries(value);
    } catch {
      return [];
    }
  }

  function safePropertyNames(value) {
    try {
      return Object.getOwnPropertyNames(value ?? {});
    } catch {
      return [];
    }
  }

  function findSkuInfo(infoMap, sku) {
    if (!infoMap || typeof infoMap !== 'object') return null;
    const keys = [sku?.skuId, sku?.id, sku?.offerSkuId].filter((value) => value !== null && value !== undefined);
    for (const key of keys) {
      if (infoMap[key]) return infoMap[key];
      if (infoMap[`sku:${key}`]) return infoMap[`sku:${key}`];
    }
    return null;
  }

  function appendPath(path, key) {
    return /^[A-Za-z_$][\w$]*$/u.test(key)
      ? `${path}.${key}`
      : `${path}[${JSON.stringify(key)}]`;
  }

  function firstText(...values) {
    const value = values.find((item) =>
      ['string', 'number'].includes(typeof item) && String(item).trim());
    return value === undefined ? '' : String(value).trim();
  }

  function firstNumber(...values) {
    for (const value of values) {
      if (value === null || value === undefined || value === '') continue;
      const match = String(value).replace(/,/g, '').match(/-?\d+(?:\.\d+)?/u);
      if (match) return Number(match[0]);
    }
    return null;
  }

  function firstInteger(...values) {
    const number = firstNumber(...values);
    return Number.isFinite(number) ? Math.trunc(number) : null;
  }

  function firstUrl(...values) {
    const raw = firstText(...values);
    if (!raw) return null;
    try {
      const url = new URL(raw, globalThis.location?.href ?? 'https://detail.1688.com/');
      return ['http:', 'https:'].includes(url.protocol) ? url.href : null;
    } catch {
      return null;
    }
  }

  globalThis.__AUTO_MAGIC_STRUCTURED_SKU_READER__ = Object.freeze({
    capture,
    extractFromObject,
    parseStaticJsonValues,
  });
})();
