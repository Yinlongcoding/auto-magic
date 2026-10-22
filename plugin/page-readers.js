// 在 1688 页面里运行的读取函数，由 collector.js 用 chrome.scripting.executeScript 注入。
// 只读页面，不点击、不改页面。字段位置来自页面探测，见设计目录 16 号"实测结果"。
// 注意：注入的是函数源码，函数体内不能引用本模块的其他变量或函数，所有工具函数都写在函数内部。

/**
 * 搜索结果页：滚动到底让卡片全部加载，返回普通卡片的商品 ID（跳过广告卡片）。
 * 在隔离环境运行即可，只读 DOM。
 */
export async function readSearchPage(maxScrollMs) {
  const sleep = (ms) => new Promise((resolve) => setTimeout(resolve, ms));
  const cardSelector = ".search-offer-item";

  // 卡片随滚动懒加载：一直滚到底，直到卡片数稳定或超时
  const deadline = Date.now() + maxScrollMs;
  let lastCount = -1;
  let stableRounds = 0;
  while (Date.now() < deadline && stableRounds < 3) {
    window.scrollTo(0, document.documentElement.scrollHeight);
    await sleep(600);
    const count = document.querySelectorAll(cardSelector).length;
    stableRounds = count === lastCount ? stableRounds + 1 : 0;
    lastCount = count;
  }

  const offerIds = [];
  let adCount = 0;
  for (const card of document.querySelectorAll(cardSelector)) {
    // 普通卡片本身是指向详情页的 <a>；广告卡片的链接是 dj.1688.com 计费跳转，取不到 ID
    const anchor = card.closest("a[href]");
    const id = anchor?.href.match(/detail\.1688\.com\/offer\/(\d+)\.html/)?.[1];
    if (id) {
      offerIds.push(id);
    } else {
      adCount++;
    }
  }

  return {
    url: location.href,
    cardCount: document.querySelectorAll(cardSelector).length,
    offerIds: [...new Set(offerIds)],
    adCount,
  };
}

/**
 * 商品详情页：读 window.context 和属性表，组装成 CollectedOffer（字段名与 C# 端一致）。
 * 必须在页面主环境（world: "MAIN"）运行，才能读到 window.context。
 * 返回 { offer, failureCode, pageOfferId }，失败时 offer 为 null。
 */
export async function readDetailPage(expectedOfferId, maxWaitMs) {
  const sleep = (ms) => new Promise((resolve) => setTimeout(resolve, ms));

  // 页面 complete 之后数据和属性表通常已就绪，这里再兜底等一会儿
  const deadline = Date.now() + maxWaitMs;
  while (Date.now() < deadline) {
    if (window.context?.result?.data && document.querySelector(".normal-attributes-table")) break;
    await sleep(250);
  }

  const data = window.context?.result?.data;
  if (!data) {
    const text = (document.body?.innerText ?? "").slice(0, 2000);
    const blocked = /验证码|安全验证|访问受限|滑块|拖动|请完成验证/.test(text);
    return { offer: null, failureCode: blocked ? "RISK_PAGE" : "NO_PAGE_DATA", pageOfferId: null };
  }

  const decoder = document.createElement("textarea");
  const decode = (s) => {
    // skuInfoMap 的键和值里 > 被转成了 &gt;。textarea 只解码实体，不执行任何内容。
    decoder.innerHTML = s;
    return decoder.value;
  };
  const toNumber = (v) => {
    const n = typeof v === "number" ? v : Number.parseFloat(v);
    return Number.isFinite(n) ? n : null;
  };

  const dataJson = data.Root?.fields?.dataJson ?? {};
  const pageOfferId = String(data.gallery?.fields?.offerId ?? "") || null;
  if (pageOfferId && pageOfferId !== expectedOfferId) {
    return { offer: null, failureCode: "ID_MISMATCH", pageOfferId };
  }

  // 价格：原价、买 1 件（13 号第 6 项），落在每个 SKU 上。
  //   rangePrice：全部 SKU 共用阶梯，取原价第一档。
  //   skuPrice：每个 SKU 单独定价，取 skuInfoMap 里该 SKU 的 price（不是促销用的 discountPrice）。
  //   其他类型不推断，留空。
  const priceType = dataJson.orderParamModel?.orderParam?.skuParam?.skuPriceType ?? null;
  const firstTier = data.mainPrice?.fields?.finalPriceModel?.tradeWithoutPromotion?.offerPriceRanges?.[0];
  const rangePrice = priceType === "rangePrice" && firstTier?.price ? String(firstTier.price) : null;
  const priceOf = (info) => priceType === "rangePrice" ? rangePrice
    : priceType === "skuPrice" && info.price ? String(info.price)
    : null;

  // 重量：千克，0 视为缺失
  const weight = toNumber(data.productPackInfo?.fields?.unitWeight);

  // 主图：去掉小图标，只留 cbu01 商品图
  const mainImages = (data.gallery?.fields?.offerImgList ?? [])
    .filter((u) => typeof u === "string" && /alicdn\.com\/img\/ibank\//.test(u));

  // 规格轴和 SKU：只取页面上真实存在的组合
  const skuModel = dataJson.skuModel ?? {};
  const axes = (skuModel.skuProps ?? []).map((prop) => ({
    name: decode(String(prop.prop ?? "")),
    options: (prop.value ?? []).map((v) => ({
      name: decode(String(v.name ?? "")),
      imageUrl: typeof v.imageUrl === "string" ? v.imageUrl : null,
    })),
  }));

  let skuParseError = false;
  const skus = Object.entries(skuModel.skuInfoMap ?? {}).map(([key, info]) => {
    const parts = String(info.specAttrs ?? key).split("&gt;").map(decode);
    if (parts.length !== axes.length) {
      skuParseError = true;
    }
    // SKU 图取第一个带图的轴上对应选项的图
    let imageUrl = null;
    axes.forEach((axis, i) => {
      imageUrl ??= axis.options.find((o) => o.name === parts[i])?.imageUrl ?? null;
    });
    return {
      skuId: String(info.skuId ?? ""),
      optionValues: parts,
      purchasePrice: priceOf(info),
      stock: Number.isInteger(info.canBookCount) ? info.canBookCount : null,
      imageUrl,
    };
  });

  // 属性表：每行是若干对 <td>名称</td><td>值</td>
  const attributes = [];
  for (const row of document.querySelectorAll(".normal-attributes-table tbody tr")) {
    const cells = [...row.children].map((cell) => (cell.textContent ?? "").replace(/\s+/g, " ").trim());
    for (let i = 0; i + 1 < cells.length; i += 2) {
      if (cells[i]) attributes.push({ label: cells[i], value: cells[i + 1] });
    }
  }

  // 品牌：只在属性表里有
  const brand = attributes.find((a) => a.label === "品牌")?.value ?? null;

  const title = String(data.productTitle?.fields?.title ?? "").trim();
  const offer = {
    offerId: expectedOfferId,
    title,
    priceType,
    weightKg: weight && weight > 0 ? weight : null,
    brand,
    mainImages,
    detailUrl: typeof data.description?.fields?.detailUrl === "string" ? data.description.fields.detailUrl : null,
    attributes,
    axes,
    skus,
  };

  // 健康检查的关键字段（16 号）：标题、至少一个 SKU、至少一张主图
  const failureCode = !title ? "NO_TITLE"
    : skus.length === 0 ? "NO_SKU"
    : skuParseError ? "SKU_PARSE"
    : mainImages.length === 0 ? "NO_IMAGE"
    : null;
  return { offer: failureCode ? null : offer, failureCode, pageOfferId };
}
