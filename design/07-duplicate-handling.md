# 重复商品处理方案

> **本文的原则（按状态分类处理）有效，但下方伪代码与数据结构已被后续决定取代。冲突时以 08、09、12 号文档为准：**
>
> 1. 状态机增加"被拒绝"。重复抓取遇到它时：更新原始数据，保留人工修改，状态退回"待人工处理"（08 问题 8）。
> 2. "已放弃"重新激活时**不清空** manual_edits，下文伪代码里的 `manual_edits: None` 作废（08 问题 2）。
> 3. 不再主动清理过期的手动修改（下文 `cleanup_obsolete_manual_edits` 作废）。人工修订按（商品、属性、源值）记录，新数据中不存在的源值自然不生效（08 问题 3）。
> 4. 数据结构以 09-final-ddl.sql 为准：manual_edits 是独立表，不是 products 的 JSON 列；ozon_offer_id 只存在于 ozon_mapping（08 问题 5）。
> 5. 更新原始数据时，按变化类型处理 ai_data：标题、描述、图片变了清对应部分并退回"待AI处理"；规格变了 ai_data 全部作废；只有价格变了则只重算价格（08 问题 1）。人工编辑过的 AI 字段保留，AI 只重新生成未编辑的字段（12 号文档）。
> 6. 价格区间外的 SKU 不删除，标记为 dropped_price；使用者改区间后可重新判断（13 号文档）。

## 确认方案：基于状态的智能处理（方案C）

---

## 核心原则

**根据商品当前状态，采用不同的处理策略**
- 已提交Ozon的 → 不动
- 本地处理中的 → 更新原始数据，保留人工修改
- 已放弃的 → 重新激活

---

## 商品生命周期

```
[已抓取] 
    ↓
[待人工处理] ←──────┐
    ↓                 │
[待AI处理]           │ 重新处理
    ↓                 │
[待提交]             │
    ↓                 │
[审核中] ─────────────┘
    ↓
[已上架]

    ↓（任何阶段都可以）
[已放弃]
```

---

## 处理策略矩阵

| 商品状态 | 处理方式 | 原因 |
|---------|---------|------|
| **新商品** | 直接保存 | 第一次见到这个1688 offerId |
| **已上架** | 跳过 | 已经在Ozon上了，不应该覆盖 |
| **审核中** | 跳过 | 已提交给Ozon，等待审核，不干扰 |
| **待提交** | 更新原始数据 + 保留人工修改 | 还在本地，可以同步1688更新 |
| **待AI处理** | 更新原始数据 + 保留人工修改 | 还在本地，可以同步1688更新 |
| **待人工处理** | 更新原始数据 + 保留人工修改 | 1688更新可能解决阻断问题 |
| **已放弃** | 重新激活 | 1688数据可能已修复，值得重试 |

---

## 数据结构设计

### 商品表结构

```sql
CREATE TABLE products (
    id INTEGER PRIMARY KEY,
    offer_id TEXT UNIQUE NOT NULL,  -- 1688商品ID（唯一标识）
    status TEXT NOT NULL,  -- 商品状态
    
    -- 1688原始数据（会被重复抓取覆盖）
    source_data JSON NOT NULL,
    -- {
    --   "title": "夏季连衣裙",
    --   "price": 50.00,
    --   "images": [...],
    --   "specs": {...}
    -- }
    
    -- 用户手动修改（永久保留）
    manual_edits JSON,
    -- {
    --   "10096": {  -- 颜色属性ID
    --     "sourceValue": "香芋紫",
    --     "mappedValueId": 3333,
    --     "note": "手动选择的紫色",
    --     "editedAt": "2026-10-19T10:30:00"
    --   }
    -- }
    
    -- 规则引擎处理结果（会重新计算）
    processed_data JSON,
    
    -- AI生成内容（会重新生成）
    ai_data JSON,
    
    -- Ozon关联信息（一旦提交就不变）
    ozon_offer_id TEXT,
    ozon_product_id TEXT,
    
    created_at DATETIME,
    updated_at DATETIME
);
```

---

## 处理流程伪代码

```python
def handle_scraped_products(scraped_products):
    """处理抓取到的1688商品列表"""
    
    stats = {
        'new': 0,           # 新商品
        'updated': 0,       # 更新的商品
        'reactivated': 0,   # 重新激活的商品
        'skipped': 0        # 跳过的商品
    }
    
    for product in scraped_products:
        existing = db.find_by_offer_id(product.offer_id)
        
        if existing is None:
            # ========== 情况1：新商品 ==========
            db.insert({
                'offer_id': product.offer_id,
                'status': '已抓取',
                'source_data': product.data,
                'manual_edits': None,
                'created_at': now()
            })
            stats['new'] += 1
            log.info(f"新增商品：{product.offer_id}")
            
        elif existing.status in ['已上架', '审核中']:
            # ========== 情况2：已提交Ozon，跳过 ==========
            stats['skipped'] += 1
            log.info(f"跳过商品：{product.offer_id}（状态：{existing.status}）")
            
        elif existing.status in ['待AI处理', '待提交', '待人工处理']:
            # ========== 情况3：本地处理中，更新原始数据 ==========
            
            # 检测1688数据是否真的变了
            if has_changes(existing.source_data, product.data):
                db.update(existing.id, {
                    'source_data': product.data,  # 覆盖1688数据
                    'manual_edits': existing.manual_edits,  # 保留用户修改
                    'updated_at': now()
                })
                
                # 重新运行规则引擎（会优先使用manual_edits的值）
                reprocess_with_rules(existing.id)
                
                stats['updated'] += 1
                log.info(f"更新商品：{product.offer_id}（检测到1688数据变化）")
            else:
                stats['skipped'] += 1
                log.debug(f"数据未变化，跳过：{product.offer_id}")
            
        elif existing.status == '已放弃':
            # ========== 情况4：重新激活 ==========
            db.update(existing.id, {
                'source_data': product.data,
                'status': '已抓取',  # 重置状态
                'manual_edits': None,  # 清空旧的手动修改
                'updated_at': now()
            })
            stats['reactivated'] += 1
            log.info(f"重新激活商品：{product.offer_id}")
    
    return stats


def reprocess_with_rules(product_id):
    """重新运行规则引擎，但优先使用用户手动修改的值"""
    
    product = db.get(product_id)
    
    # 运行规则引擎
    processed = rule_engine.process(
        source_data=product.source_data,
        manual_overrides=product.manual_edits  # 传入手动修改
    )
    
    # 规则引擎内部逻辑：
    # 对于每个属性：
    #   1. 检查 manual_overrides 中是否有该属性
    #   2. 有 → 使用手动值，跳过规则匹配
    #   3. 无 → 正常运行规则
    
    db.update(product_id, {
        'processed_data': processed,
        'status': determine_status(processed)  # 根据是否有阻断决定状态
    })
```

---

## 用户界面提示

### 抓取完成提示

```
┌─────────────────────────────────────────────┐
│ 抓取完成                                     │
├─────────────────────────────────────────────┤
│ ✅ 新增商品：20个                            │
│ 🔄 更新商品：15个                            │
│    （1688数据已更新，保留了您的手动修改）     │
│ 🔁 重新激活：3个                             │
│    （之前放弃的商品，1688数据已改善）         │
│ ⏭️  跳过商品：12个                           │
│    （已上架或审核中，未修改）                 │
├─────────────────────────────────────────────┤
│ 总计：待处理 38个商品                        │
│                                              │
│ [查看待处理列表]  [开始处理]                 │
└─────────────────────────────────────────────┘
```

### 更新详情（可展开查看）

```
更新的商品列表（15个）：

📦 夏季连衣裙（offerId: 123456）
   • 标题：夏季连衣裙 → 爆款连衣裙
   • 价格：¥50.00 → ¥45.00
   • 保留修改：颜色映射"香芋紫→紫色"

📦 宽松T恤（offerId: 789012）
   • 新增规格：增加了"XL"尺码
   • 保留修改：品牌"无品牌"

...
```

---

## 检测1688数据变化

### 需要检测的字段

```python
def has_changes(old_data, new_data):
    """检测1688商品数据是否发生变化"""
    
    # 关键字段比对
    fields_to_check = [
        'title',        # 标题
        'price',        # 价格
        'images',       # 图片URL列表
        'specs',        # 规格信息
        'description'   # 详情描述（可选）
    ]
    
    for field in fields_to_check:
        if old_data.get(field) != new_data.get(field):
            return True
    
    return False
```

**为什么要检测变化**：
- 避免无意义的数据库更新
- 避免重新运行规则引擎（如果数据没变，结果也不会变）
- 提高性能

---

## 保留人工修改的实现

### 规则引擎内部逻辑

```python
def process_attribute(attribute_id, source_data, manual_overrides):
    """处理单个属性"""
    
    # 1. 检查是否有人工修改
    if attribute_id in manual_overrides:
        manual = manual_overrides[attribute_id]
        return {
            'value': manual['sourceValue'],
            'valueId': manual['mappedValueId'],
            'status': 'manual',  # 标记为人工设置
            'note': f"人工修改于 {manual['editedAt']}"
        }
    
    # 2. 没有人工修改，正常运行规则
    rule = load_rule(attribute_id)
    source_value = extract_value(source_data, rule.fields)
    
    if source_value is None:
        # 使用fallback
        return apply_fallback(rule)
    
    # 查找映射
    mapping = find_mapping(rule, source_value)
    if mapping:
        return {
            'value': source_value,
            'valueId': mapping.valueId,
            'status': 'matched'
        }
    else:
        return {
            'value': source_value,
            'valueId': None,
            'status': 'unmatched',
            'isBlocking': rule.is_required
        }
```

---

## 边界情况处理

### 情况1：人工修改的属性在新数据中消失了

**例子**：
- 第一次抓取：商品有"红色"规格，用户手动映射了
- 第二次抓取：商品取消了"红色"规格，只剩"黑色"

**处理**：
- 保留 `manual_edits` 中的"红色"记录（历史审计）
- 但在重新处理时，如果SKU列表中已经没有"红色"了，该手动修改不会生效
- 系统只处理当前存在的SKU

---

### 情况2：1688商品改了offerId

**例子**：
- 卖家删除旧商品，重新上架，offerId变了

**处理**：
- 系统无法识别这是同一个商品
- 会被当作新商品处理
- **这是1688平台的限制，无法解决**

---

### 情况3：用户手动修改后，1688数据更新，阻断自动解决

**例子**：
- 第一次抓取：尺码"均码"，用户手动选择了"42码"
- 第二次抓取：1688增加了"S/M/L"规格，"均码"消失了

**处理**：
- 更新原始数据（现在是S/M/L）
- 清除旧的手动修改（因为"均码"已经不存在了）
- 重新运行规则引擎，S/M/L可以自动映射
- 商品从"待人工处理"变为"待AI处理"

**实现**：
```python
# 清理过期的手动修改
def cleanup_obsolete_manual_edits(product):
    current_skus = extract_skus(product.source_data)
    new_manual_edits = {}
    
    for attr_id, edit in product.manual_edits.items():
        # 检查该手动修改对应的SKU是否还存在
        if is_still_relevant(edit, current_skus):
            new_manual_edits[attr_id] = edit
        else:
            log.info(f"清除过期的手动修改：{edit}")
    
    return new_manual_edits
```

---

## 优势总结

✅ **不丢失用户工作**：手动修改的属性值被永久保留
✅ **自动同步更新**：1688价格、标题、图片变化会自动同步
✅ **避免冲突**：已提交Ozon的商品不动，保证数据一致性
✅ **智能修复**：1688数据改善后，可能自动解决阻断问题
✅ **全自动处理**：无需人工决策，提高批量处理效率
✅ **可追溯**：保留手动修改的时间戳，便于审计

---

## 实现优先级

1. **基础去重**：根据offerId判断重复，新商品插入，重复跳过
2. **状态区分**：实现状态机，区分"已上架"、"待处理"等状态
3. **更新逻辑**：实现"更新原始数据 + 保留手动修改"
4. **变化检测**：避免无意义的更新（性能优化）
5. **界面提示**：显示详细的统计信息

---

这个方案已确认，可以作为开发依据。
