# AM 数据清洗预览

在“字段匹配”选中采集商品后，“数据清洗 JSON”自动展示本地整理结果；无需先读取 Ozon Schema，也不会调用 AI。切换商品时同步更新。

```json
{
  "sourceProduct": {
    "platform": "1688",
    "offerId": "123456",
    "title": "女士连衣裙",
    "detailUrl": "https://detail.1688.com/offer/123456.html",
    "capturedAt": "2026-09-22T00:00:00Z",
    "captureStatus": "partial"
  },
  "confirmedFacts": [
    { "factId": "f002", "label": "颜色", "rawValue": ["红色", "蓝色"], "sourcePath": "$.facts[1]" },
    { "factId": "f003", "label": "尺码", "rawValue": ["S", "M"], "sourcePath": "$.facts[2]" }
  ],
  "uncertainFacts": [
    { "label": "适用性别", "candidateValue": "女", "reason": "由 inference:title 得到的候选值，尚不是直接采集的事实。", "evidenceFactIds": ["f001"] }
  ],
  "skuCombinations": [
    { "combinationKey": "red-S", "verification": "dimension-cartesian", "options": { "颜色": "红色", "尺码": "S" } }
  ]
}
```

`confirmedFacts` 保存采集的原始字段；当采集器给出同名规格维度时，将该字段展示为选项数组。若规格维度没有对应普通字段，则补充一条可追溯到维度路径的确认事实。推断、产地转换和无法识别的颜色选项列在 `uncertainFacts`，不会冒充原始确认事实。`skuCombinations` 保留采集器的全部组合及 `verification`；`dimension-cartesian` 表示由规格轴生成，并不证明每个组合可售。当前视图不展示图片、库存、价格，也不含 Ozon 映射结果。
