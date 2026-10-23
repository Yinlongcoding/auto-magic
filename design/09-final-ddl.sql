-- Auto Magic 定稿 DDL（SQLite）。依据 08-database-schema.md 的 8 条已确认修正。
-- 规则类目映射不在库内，见 rules/category-mapping.json。

PRAGMA foreign_keys = ON;

-- 状态：captured 已抓取 / need_manual 待人工处理 / need_ai 待AI处理 / ready 待提交
--       reviewing 审核中 / listed 已上架 / rejected 被拒绝 / abandoned 已放弃
CREATE TABLE products (
    id                INTEGER PRIMARY KEY AUTOINCREMENT,
    offer_id          TEXT NOT NULL UNIQUE,            -- 1688 商品 ID，全局唯一（问题8）
    status            TEXT NOT NULL CHECK (status IN
        ('captured','need_manual','need_ai','ready','reviewing','listed','rejected','abandoned')),
    ozon_category_id  INTEGER NOT NULL,
    ozon_type_id      INTEGER NOT NULL,                -- 不设外键（问题7）
    source_data       TEXT NOT NULL,                   -- 1688 原始数据 JSON
    processed_data    TEXT,                            -- 仅商品级属性（问题4）
    ai_data           TEXT,                            -- 变化时按 08 问题1 的规则失效
    price_config      TEXT,
    created_at        DATETIME NOT NULL DEFAULT CURRENT_TIMESTAMP,
    updated_at        DATETIME NOT NULL DEFAULT CURRENT_TIMESTAMP,
    submitted_at      DATETIME
);
CREATE INDEX idx_products_status ON products(status);
CREATE INDEX idx_products_type   ON products(ozon_type_id);

CREATE TABLE product_skus (
    id             INTEGER PRIMARY KEY AUTOINCREMENT,
    product_id     INTEGER NOT NULL REFERENCES products(id) ON DELETE CASCADE,
    sku_key        TEXT NOT NULL,
    spec_values    TEXT NOT NULL,                      -- {"颜色":"黑色","尺码":"S"}
    source_price   REAL,
    attributes     TEXT,                               -- 仅来自规格轴的属性（问题4）
    final_price    REAL,
    stock_quantity INTEGER NOT NULL DEFAULT 99,
    image_urls     TEXT,
    -- 价格筛选状态：active 参与流程；dropped_price 因价格不在任务区间被丢弃，保留行供重复抓取对应
    sku_state      TEXT NOT NULL DEFAULT 'active' CHECK (sku_state IN ('active','dropped_price')),
    drop_reason    TEXT,                               -- 丢弃时的价格依据（采购价、区间、实际利润率）
    UNIQUE (product_id, sku_key)
);

-- 按（商品、属性、源值）记录（问题3）。商品级属性 source_value 用空串；
-- sku_key 为空串表示作用于所有包含该源值的 SKU，非空表示仅覆盖该 SKU。
CREATE TABLE manual_edits (
    id               INTEGER PRIMARY KEY AUTOINCREMENT,
    product_id       INTEGER NOT NULL REFERENCES products(id) ON DELETE CASCADE,
    attribute_id     INTEGER NOT NULL,
    source_value     TEXT NOT NULL DEFAULT '',
    sku_key          TEXT NOT NULL DEFAULT '',
    edited_value     TEXT NOT NULL,
    edited_value_id  INTEGER,
    note             TEXT,
    promoted_to_rule INTEGER NOT NULL DEFAULT 0,
    promoted_at      DATETIME,
    edited_at        DATETIME NOT NULL DEFAULT CURRENT_TIMESTAMP,
    UNIQUE (product_id, attribute_id, source_value, sku_key)
);

-- Ozon 标识只在此表保存一份（问题5），按 SKU 行 ID 关联。
CREATE TABLE ozon_mapping (
    id               INTEGER PRIMARY KEY AUTOINCREMENT,
    sku_id           INTEGER NOT NULL UNIQUE REFERENCES product_skus(id) ON DELETE CASCADE,
    ozon_offer_id    TEXT NOT NULL UNIQUE,
    ozon_product_id  TEXT,
    ozon_task_id     TEXT,
    submit_status    TEXT CHECK (submit_status IN ('pending','importing','success','failed')),
    import_result    TEXT,
    review_status    TEXT CHECK (review_status IN ('reviewing','approved','rejected')),
    review_message   TEXT,
    created_at       DATETIME NOT NULL DEFAULT CURRENT_TIMESTAMP,
    submitted_at     DATETIME,
    imported_at      DATETIME,
    reviewed_at      DATETIME
);
CREATE INDEX idx_mapping_status ON ozon_mapping(submit_status, review_status);

-- offer_id 首次写入后只读，保证重复提交是更新而不是新建。
CREATE TRIGGER trg_ozon_offer_id_readonly
BEFORE UPDATE OF ozon_offer_id ON ozon_mapping
BEGIN
    SELECT RAISE(ABORT, 'ozon_offer_id is immutable');
END;

-- Schema 缓存（问题7）。schema_data 必须含 is_required / is_aspect，
-- 缺少时由代码拒绝并要求刷新（DDL 无法表达）。
CREATE TABLE type_schema_cache (
    category_id INTEGER NOT NULL,
    type_id     INTEGER NOT NULL,
    schema_data TEXT NOT NULL,
    fetched_at  DATETIME NOT NULL DEFAULT CURRENT_TIMESTAMP,
    PRIMARY KEY (category_id, type_id)
);

-- 字典缓存分两档（问题6）。dictionary_fetch 记录某字典的缓存方式，
-- 使 ContainsAsync 能区分：full 档缺失 = 不适用；on_demand 档缺失 = 未知。
CREATE TABLE dictionary_fetch (
    category_id   INTEGER NOT NULL,
    type_id       INTEGER NOT NULL,
    dictionary_id INTEGER NOT NULL,
    mode          TEXT NOT NULL CHECK (mode IN ('full','on_demand')),
    fetched_at    DATETIME NOT NULL DEFAULT CURRENT_TIMESTAMP,
    PRIMARY KEY (category_id, type_id, dictionary_id)
);

CREATE TABLE dictionary_cache (
    category_id   INTEGER NOT NULL,
    type_id       INTEGER NOT NULL,
    dictionary_id INTEGER NOT NULL,
    value_id      INTEGER NOT NULL,
    value_text    TEXT NOT NULL,
    origin        TEXT NOT NULL CHECK (origin IN ('full','verified','manual')),
    cached_at     DATETIME NOT NULL DEFAULT CURRENT_TIMESTAMP,
    PRIMARY KEY (category_id, type_id, dictionary_id, value_id)
);

CREATE TABLE settings (
    key        TEXT PRIMARY KEY,
    value      TEXT NOT NULL,
    updated_at DATETIME NOT NULL DEFAULT CURRENT_TIMESTAMP
);
-- 默认值：require_manual_review=false，scrape_count=50。密钥不放此表，另行保管。

CREATE TABLE tasks (
    id             INTEGER PRIMARY KEY AUTOINCREMENT,
    name           TEXT NOT NULL,
    type_id        INTEGER NOT NULL,                   -- 不设外键（问题7）
    search_keyword TEXT NOT NULL,
    status         TEXT NOT NULL DEFAULT 'active' CHECK (status IN ('active','completed','archived')),
    created_at     DATETIME NOT NULL DEFAULT CURRENT_TIMESTAMP,
    completed_at   DATETIME
);

CREATE TABLE task_products (
    task_id    INTEGER NOT NULL REFERENCES tasks(id) ON DELETE CASCADE,
    product_id INTEGER NOT NULL REFERENCES products(id) ON DELETE CASCADE,
    PRIMARY KEY (task_id, product_id)
);
