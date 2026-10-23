# /v3/product/import 请求体要点（来源：用户提供的官方示例与字段说明）

来源为用户从 Ozon 官方文档页复制的 JSON，非我直接抓取。响应结构、task_id、/v1/product/import/info 仍未核实。

## Schema 标记为必填的字段（仅 4 个）

description_category_id、type_id、offer_id、price

## 其余字段（Schema 未标必填，是否被导入校验强制要求：未核实）

| 字段 | 类型 | 要点 |
|---|---|---|
| name | string | 最长 500 字符 |
| attributes | array | 元素：id、complex_id、values[{dictionary_value_id, value}]；具体必填由类型决定，用 Schema 的 is_required 判断 |
| images | string[] | 最多 30 张，JPG 或 PNG，须为公开云存储链接；设了 primary_image 则最多 29 张 |
| primary_image | string | 主图链接 |
| depth / width / height | int | 包装尺寸，整数 |
| dimension_unit | string | mm / cm / in |
| weight | int | 含包装重量，整数 |
| weight_unit | string | g / kg / lb |
| vat | string | 0 / 0.05 / 0.07 / 0.1 / 0.2 / 0.22 |
| currency_code | string | 必须与卖家后台设置的币种一致，默认 RUB，不一致会报错 |
| old_price | string | 划线价，必须大于 price |
| barcode、color_image、pdf_list、promotions、complex_attributes | - | 可选 |

## 具体约束

- offer_id 最长 50 字符。字符集限制文档未说明。
- price、old_price、vat 是字符串，尺寸和重量是整数。
- 填了 type_id 后，attributes 里可以省略 id 为 8229 的属性（类型属性）。
- 不要使用 geo_names（已废弃）。

## 对现有设计的影响

1. offer_id 的 8 位前缀加 4 位哈希共 13 字符，远低于 50，"offer_id 长度未核实"一项已解决长度部分。
2. settings 需要增加 currency_code，并与卖家后台币种一致。汇率换算的目标币种由它决定。
3. 8229 可能不用清洗规则：给了 type_id 就可以省略，仓库里的 rules/attributes/8229.json 需要确认是否仍有用途。
4. 03 的阻断点里，尺寸、重量、图片、name 不在 Schema 必填之列。在首次真实调用确认前，仍按阻断处理（保守做法），确认后再放宽。
5. 价格、尺寸、重量的数据类型要在组装请求时做类型转换，数据库内可用数值存储。

## 提交与轮询的落库策略（已确认：提交即落库）

1. 提交前先把 ozon_mapping 的 submit_status 写成 pending。
2. 收到 task_id 后写入 ozon_task_id，状态改为 importing。
3. 应用启动时扫描 importing 记录，按 task_id 继续轮询。
4. 应用启动时发现 pending 且没有 task_id 的记录，说明请求发出前后进程中断，无法确认 Ozon 是否收到。因为 offer_id 固定，重新提交等同于更新，不会重复建卡，所以直接重提。该推断依赖"相同 offer_id 重复提交是更新"，已记录在 docs/pipeline-design.md，未经真实调用验证。

## 导入错误的回挂（已确认：按错误类型分流）

- 属性类错误（按 offer_id 和属性 ID 定位）：商品退回"待人工处理"，错误挂在对应属性上，进属性补全界面。
- 非属性类错误（图片、尺寸、重量、价格等）：单独标记，不进属性补全界面，在商品详情里单独展示。
- 分流目前只定到"属性类 / 非属性类"两层。Ozon 错误响应的实际结构未核实，细分类和判定字段留到首次真实调用拿到错误样本后再定。
- 错误原文写入 ozon_mapping.import_result 保存，供后续分析。

## 限流与重试（已确认：按接口区分）

- 查询类接口（拉字典、拉 Schema、查导入状态）：遇到 429、5xx、网络超时做指数退避，最多重试 3 次。只读，重试无副作用。
- 提交类接口（/v3/product/import）：不自动重试。超时不代表 Ozon 没收到，失败后记录状态，交给用户手动重提或启动恢复逻辑处理。
- 其他 4xx 错误一律不重试，直接报错。
- Ozon 的具体限流数值未核实，首次真实调用时确认。

## 与前面"启动恢复直接重提"的关系

启动时对"pending 且无 task_id"的记录直接重提，是用户可见的恢复动作，不是静默自动重试，与上面"提交不自动重试"不冲突。两者都依赖"相同 offer_id 重提是更新"这个未验证前提，首次真实调用时要优先验证。

## 8292 合并卡片值（已确认：复用 offer_id 前缀）

- 值为源商品 ID 的 36 进制编码，固定 8 位，如 BTCJEX28。去掉原来的 `1688-` 前缀，避免把货源平台写进提交数据。
- 它同时是该商品所有 offer_id 的前缀（offer_id 形如 BTCJEX28-7K2Q），全项目共用一个编码函数。
- 同一采集商品的所有 SKU 共用该值；由 products.offer_id 推出，不单独落库；系统生成，不进阻断列表。
- Ozon 对长度和字符集的限制未核实，已加入首次调用验证清单。

## 仍未核实

- 导入响应结构、task_id、状态查询接口的字段
- name、images、尺寸、重量缺失时是拒绝导入还是导入后标记不完整
- v3ImportProductsRequestAttribute 的完整定义（上面只是从示例推断）
