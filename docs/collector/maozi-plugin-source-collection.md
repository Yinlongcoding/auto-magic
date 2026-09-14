# 毛子ERP：独立货源采集逻辑

本文只从 `maozi-plugin-3.2.6` 的静态代码中拆出“货源商品采集”功能。它不包含 Ozon/WB 跟卖、定价、Cookie 绑定、字段匹配或实际发布逻辑。

## 1. 模块边界

插件在下列商品详情页显示两个操作：`采集商品` 与 `复制图片`。

| 货源 | 详情页识别 | `collect_from` |
| --- | --- | --- |
| 1688 | `detail.1688.com/offer...` | `1688` |
| 淘宝 | `item.taobao.com/item.htm` | `taobao` |
| 天猫 | `detail.tmall.com/item.htm` | `tmall` |
| 拼多多 | `goods.html` / `goods2.html` | `pdd` |
| 京东 | `item.jd.com/...` | `jd` |
| AliExpress 俄罗斯站 | `/item/` 或 `/_detail/` | `aliexpress` |
| Amazon | `/dp/` 或 `/gp/` | `amz` |

每个站点的 DOM/API 解析方式不同，但最终都被整理为同一个 `CollectedSourceProduct` 结构，然后发送至毛子ERP服务端。

## 2. 统一输出合同

```ts
type CollectedSourceProduct = {
  // 保留站点接口响应或 DOM 解析产物，供服务端复核/补全。
  origin_data: unknown;

  data: {
    url: string;
    goods_id: string;
    goods_name: string;
    description: {
      text: string;
      images: string[];
    };
    images: string[];
    video: string;
    skus: Array<{
      name: string;
      price: number;
      primary_image: string;
      video: string;
    }>;
    attributes: Array<{
      name: string;
      value: string;
    }>;
    productPackInfo: {
      weight: number;
    };
  };

  collect_from: "1688" | "taobao" | "tmall" | "pdd" | "jd" | "aliexpress" | "amz";
};
```

提交方式：

```http
POST https://api.maozierp.com/api.chrome/collect
Authorization: Bearer <maozierp-token>
Content-Type: application/json
Client: plugin
Plugin-Version: 3.2.6
```

请求体就是完整的 `CollectedSourceProduct`。插件判断响应的 `code === 1` 为采集成功。

## 3. 可读的核心流程

```js
async function collectSourceProduct(adapter, api) {
  // 1. 当前页必须是该货源站点的商品详情页，且页面已加载。
  const raw = await adapter.readProductPage();

  // 2. 站点特定的原始字段，归一为统一商品事实。
  const product = normalizeCollectedProduct(raw, adapter.platform);

  // 3. 采集时至少要求标题和图片；部分适配器还要求有效 SKU。
  if (!product.data.goods_name || product.data.images.length === 0) {
    throw new Error("货源商品数据不完整");
  }

  // 4. 原插件把整个对象交给后端，而不是在浏览器中映射 Ozon 属性。
  const response = await api.post("/api.chrome/collect", product);
  if (response.code !== 1) throw new Error(response.msg || "采集失败");
  return product;
}

function normalizeCollectedProduct(raw, platform) {
  const attributes = raw.attributes ?? [];
  const weightAttribute = attributes.find(({ name }) =>
    name.includes("重量") || name.includes("净重"),
  );

  return {
    origin_data: raw.origin_data ?? raw,
    data: {
      url: raw.url,
      goods_id: raw.goods_id ?? "",
      goods_name: raw.goods_name ?? "",
      description: raw.description ?? { text: "", images: [] },
      images: uniqueHttpUrls(raw.images ?? []),
      video: raw.video ?? "",
      skus: (raw.skus ?? []).map(normalizeSku),
      attributes,
      productPackInfo: {
        weight: firstNumber(weightAttribute?.value) ?? 0,
      },
    },
    collect_from: platform,
  };
}

function normalizeSku(sku) {
  return {
    name: sku.name ?? "",
    price: Number(sku.price) || 0,
    primary_image: sku.primary_image ?? "",
    video: sku.video ?? "",
  };
}
```

`uniqueHttpUrls`、`firstNumber` 是为说明逻辑写出的等价辅助函数；原插件对不同站点分别清理图片 URL 和提取重量。

## 4. 各站适配点

### 1688

- 从商品 DOM 读取标题、轮播图、价格、规格、详情图和 `#productPackInfo` 重量。
- 规格有多种选择器兜底；无规格时生成单一 SKU，名称为商品标题、图片为首张主图。
- 输出 `origin_data.collected_by = "dom_parser"` 与采集时间。

### 淘宝 / 天猫

- 调用商品详情和详情描述接口，而不仅仅读 DOM。
- 从 `skuBase.props` 取得规格名和规格图，从 `skuCore.sku2info` 取得 SKU 价格。
- 用 SKU 的 `propPath` 拼接规格名称；若找不到规格图，回退至首张主图。
- 详情富文本被转为纯文本，`<img src>` 同时收集至 `description.images`。

### 拼多多

- 注入页面上下文脚本读取 `window.rawData`；失败时调用站内 SKU 接口兜底。
- 读取商品图片、视频、SKU、规格描述；以 `name:value` 文本拆分属性。

### 京东

- 监听页面网络/DOM 完成，解析商品规格控件及组合。
- 读取商品主图、SKU 图、价格、详情区域和属性表；将规格选项做组合以构成 SKU。

### AliExpress / Amazon

- 优先从页面请求或商品结构读取标题、图片、变体及价格；不足时回退 DOM。
- AliExpress 在详情图为空且主图超过五张时，将第六张及之后的主图当作详情图回填。
- Amazon 的图片会补入 SKU 主图中未存在于主图列表的 URL。

## 5. 明确不属于此模块的内容

- 没有任何浏览器端“货源字段 → Ozon 属性 ID”的映射；
- 没有调用 Ozon 的发布接口；
- 没有选择 Ozon 店铺、仓库、上架方式或 Ozon 定价；
- `api.chrome/collect` 返回后的清洗、翻译、类目映射和草稿创建，都不可从该插件包确定，应视为服务端逻辑。

## 6. 复用建议

若要在自己的插件中复用这一设计，建议把每个站点实现为 `readProductPage()` 适配器，并把以上统一合同作为唯一输出。采集层只记录来源事实和证据；任何 AI 翻译、Ozon 类目/属性匹配、价格策略及发布，应放在下游模块并保留独立的输入输出。
