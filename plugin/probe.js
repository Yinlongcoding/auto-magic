// 页面探测（第 2 版）：只在用户点按钮时读取当前页面，不导航、不翻页、不点击。
// 输出页面结构摘要（选择器、字段路径、少量样例值），不含 cookie 或登录信息。
// 探测完成后这个文件和 popup 一起删除。

// 注意：executeScript 注入的是函数源码，函数里不能引用本模块的其他变量，所以常量和工具函数都定义在函数内部。
export function probePage() {
  const MAX = { list: 10, text: 200, hits: 160, urls: 40 };
  const out = {
    probeVersion: 3,
    capturedAt: new Date().toISOString(),
    url: location.href,
    host: location.host,
    path: location.pathname,
    search: Object.fromEntries(new URLSearchParams(location.search)),
    documentTitle: document.title,
    readyState: document.readyState,
    scrollY: Math.round(window.scrollY),
    pageHeight: document.documentElement.scrollHeight,
    pageKind: "unknown",
  };

  const text = (el, n = MAX.text) => (el?.textContent ?? "").replace(/\s+/g, " ").trim().slice(0, n);
  const all = (selector, root = document) => {
    try { return [...root.querySelectorAll(selector)]; } catch { return []; }
  };
  const imgSrc = (el) => el?.currentSrc || el?.src || el?.getAttribute("data-src") || el?.getAttribute("data-lazy-src") || "";
  const bgUrl = (el) => (getComputedStyle(el).backgroundImage.match(/url\("?(.*?)"?\)/) ?? [])[1] ?? "";
  const offerIdOf = (href) => href.match(/\/offer\/(\d+)\.html/)?.[1] ?? href.match(/offerIds?=(\d+)/)?.[1] ?? null;

  if (location.pathname.includes("offer_search")) {
    out.pageKind = "search";
    out.searchPage = probeSearch();
  } else if (location.pathname.includes("/offer/")) {
    out.pageKind = "detail";
    out.detailPage = probeDetail();
  } else {
    out.pageKind = location.href.includes("login") ? "login" : "other";
  }

  const bodyText = (document.body?.innerText ?? "").slice(0, 1500);
  out.risk = {
    urlSignals: ["login", "punish", "x5sec", "verify", "captcha", "baxia"].filter((s) => location.href.toLowerCase().includes(s)),
    textSignals: ["验证码", "安全验证", "访问受限", "滑块", "请登录", "登录后", "拖动"].filter((s) => bodyText.includes(s)),
    hasLoginForm: all("input[type=password]").length > 0,
    bodyStart: bodyText.slice(0, 160),
  };
  return out;

  function probeSearch() {
    const cards = all(".search-offer-item");
    return {
      cardCount: cards.length,
      cards: cards.slice(0, MAX.list).map((card) => {
        const anchor = card.closest("a[href]") ?? (card.matches("a[href]") ? card : null);
        const hrefs = [anchor?.href, ...all("a[href]", card).map((a) => a.href)].filter(Boolean);
        const id = hrefs.map(offerIdOf).find(Boolean) ?? null;
        return {
          tag: card.tagName,
          className: card.className,
          offerId: id,
          ownHref: anchor?.href?.slice(0, 200) ?? null,
          isAd: Boolean(anchor?.href?.includes("dj.1688.com")) || /(^|[\s-])ad([\s-]|$)/.test(card.className),
          title: text(card.querySelector(".title-text")),
          price: text(card.querySelector(".text-main")),
          priceBlock: text(card.querySelector("[class*=price]"), 80),
        };
      }),
      // 翻页控件：类名里含 pag 或 next 的元素
      pagination: all("[class*=pag] a, [class*=pag] button, [class*=pag] span, [class*=next]")
        .slice(0, 20)
        .map((el) => ({ tag: el.tagName, className: String(el.className).slice(0, 80), text: text(el, 20), href: el.href ?? null })),
      sortSelected: all(".sm-sort-item.sm-sort-selected").map((el) => text(el, 20)),
      priceInputs: all(".price-filter-form input").slice(0, 2).map((el) => el.value),
    };
  }

  function probeDetail() {
    const offerIdFromUrl = location.pathname.match(/\/offer\/(\d+)/)?.[1] ?? null;
    const detail = { offerIdFromUrl };

    // 属性表：每行的单元格分开记录，确认 label/value 的切分方式
    detail.attributeRows = all(".normal-attributes-table tbody tr").slice(0, 6)
      .map((tr) => [...tr.children].map((cell) => `${cell.tagName}:${text(cell, 40)}`));
    detail.attributeRowCount = all(".normal-attributes-table tbody tr").length;

    // 主图：去重后的地址列表
    const galleryUrls = [
      ...all(".od-picture-gallery-list img").map(imgSrc),
      ...all(".od-picture-gallery-list .v-image-cover").map(bgUrl),
      ...all("[class*=gallery] img").map(imgSrc),
    ].filter((u) => u && !u.startsWith("data:"));
    detail.gallery = {
      itemCount: all(".od-picture-gallery-list .v-image-cover").length,
      uniqueUrls: [...new Set(galleryUrls)].slice(0, 20),
    };

    // 详情图：图片、iframe 和含 desc/detail 的容器
    detail.description = {
      imgInDesc: all("[id*=desc] img, [class*=desc] img, #detailContent img, .detail-content img").length,
      sampleImgs: all("[id*=desc] img, [class*=desc] img, #detailContent img, .detail-content img").slice(0, 3).map(imgSrc),
      iframes: all("iframe").slice(0, 5).map((f) => f.src.slice(0, 160)),
      containers: all("[id*=desc], [class*=description], [class*=detail-desc]").slice(0, 6)
        .map((el) => `${el.tagName}#${el.id}.${String(el.className).slice(0, 60)} imgs=${all("img", el).length}`),
    };

    detail.embedded = probeEmbedded();
    return detail;
  }

  // 找到含 skuModel 的内联脚本，解析出其中的 JSON 对象，记录变量名、顶层结构和相关字段路径
  function probeEmbedded() {
    const script = all("script:not([src])").find((s) => (s.textContent ?? "").includes("skuModel"));
    if (!script) {
      return { found: false };
    }

    const source = script.textContent;
    const result = { found: true, length: source.length, head: source.slice(0, 300) };

    // 优先读页面已经算好的 window.context（需要在页面主环境运行，popup 用 world: "MAIN" 注入）。
    // 读不到再退回解析脚本文本，并记下解析失败的原因。
    let data = null;
    try {
      if (window.context?.result?.data) {
        data = window.context;
        result.source = "window.context";
      }
    } catch (error) {
      result.contextError = String(error);
    }

    if (!data) {
      const parsed = parseFirstObject(source);
      data = parsed.data;
      result.source = data ? "script text" : null;
      result.parseErrors = parsed.errors;
    }

    if (!data) {
      result.parsed = false;
      return result;
    }

    result.parsed = true;
    // result.data 下是一个个模块（如 productPackInfo），每个模块有 fields。列到 fields 的字段名和类型。
    result.modules = describe(data?.result?.data ?? data, 3);

    // 和价格、重量、尺寸、详情、品牌、起订量相关的字段路径与样例值
    const pattern = /price|amount|weight|volume|length|width|height|size|unit|desc|detail.*url|brand|begin|minOrder|moq|freight|ladder|range/i;
    const hits = [];
    walk(data, "", 0, (path, key, value) => {
      if (hits.length < MAX.hits && pattern.test(key) && (value === null || typeof value !== "object" || Array.isArray(value) && value.length <= 5)) {
        hits.push(`${path} = ${JSON.stringify(value)?.slice(0, 160)}`);
      }
    });
    result.fieldHits = hits;

    // 所有 http 地址字段的路径，用来找主图列表和详情（描述）地址
    const urls = [];
    walk(data, "", 0, (path, _key, value) => {
      if (urls.length < MAX.urls && typeof value === "string" && /^(https?:)?\/\//.test(value) && !path.includes("skuModel")) {
        urls.push(`${path} = ${value.slice(0, 160)}`);
      }
    });
    result.urlHits = urls;

    // skuModel 原样带回，skuInfoMap 只留前 4 条
    const skuModel = findKey(data, "skuModel");
    if (skuModel) {
      const map = skuModel.skuInfoMap ?? {};
      result.skuModel = {
        ...skuModel,
        skuInfoMap: Object.fromEntries(Object.entries(map).slice(0, 4)),
        skuInfoMapSize: Object.keys(map).length,
      };
    }
    return result;
  }

  function parseFirstObject(source) {
    // 数据是函数实参：window.context=(function(b,d){...})(window.contextPath,{"result":...})。
    // 从每个跟在 = ( , 后面的 "{" 尝试截取配平的大括号，能 JSON.parse 且含 skuModel 就用。
    const starts = [];
    const errors = [];
    for (const m of source.matchAll(/[=(,]\s*\{/g)) starts.push(m.index + m[0].length - 1);
    if (source.trimStart().startsWith("{")) starts.unshift(source.indexOf("{"));
    for (const start of starts.slice(0, 60)) {
      const end = matchBrace(source, start);
      if (end < 0) {
        if (source.slice(start, start + 200000).includes("skuModel")) {
          errors.push(`start=${start}: 大括号不配平`);
        }
        continue;
      }
      const candidate = source.slice(start, end + 1);
      if (!candidate.includes("skuModel")) continue;
      try {
        return { data: JSON.parse(candidate), errors };
      } catch (error) {
        // 记下出错位置附近的原文，用来判断是哪种非标准写法
        const at = Number(String(error.message).match(/position (\d+)/)?.[1] ?? -1);
        errors.push(`start=${start} len=${candidate.length}: ${error.message} near: ${at >= 0 ? candidate.slice(Math.max(0, at - 60), at + 60) : ""}`);
      }
      if (errors.length >= 3) break;
    }
    return { data: null, errors };
  }

  function matchBrace(s, start) {
    let depth = 0;
    let inString = false;
    for (let i = start; i < s.length; i++) {
      const c = s[i];
      if (inString) {
        if (c === "\\") i++;
        else if (c === "\"") inString = false;
      } else if (c === "\"") inString = true;
      else if (c === "{") depth++;
      else if (c === "}" && --depth === 0) return i;
    }
    return -1;
  }

  function describe(value, depth) {
    if (value === null || typeof value !== "object") return typeof value;
    if (Array.isArray(value)) return [`array(${value.length})`, value.length > 0 && depth > 0 ? describe(value[0], depth - 1) : null];
    if (depth <= 0) return `object(${Object.keys(value).length} keys)`;
    return Object.fromEntries(Object.keys(value).slice(0, 50).map((k) => [k, describe(value[k], depth - 1)]));
  }

  function walk(value, path, depth, visit) {
    if (depth > 10 || value === null || typeof value !== "object") return;
    const entries = Array.isArray(value) ? value.slice(0, 3).map((v, i) => [String(i), v]) : Object.entries(value);
    for (const [key, child] of entries) {
      const childPath = path ? `${path}.${key}` : key;
      visit(childPath, key, child);
      walk(child, childPath, depth + 1, visit);
    }
  }

  function findKey(value, name, depth = 0) {
    if (depth > 10 || value === null || typeof value !== "object") return null;
    if (!Array.isArray(value) && name in value) return value[name];
    for (const child of Object.values(value)) {
      const found = findKey(child, name, depth + 1);
      if (found) return found;
    }
    return null;
  }
}
