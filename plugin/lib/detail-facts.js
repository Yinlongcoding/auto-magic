const DEFAULT_MAX_FACTS = 500;
const MAX_LABEL_LENGTH = 120;
const MAX_VALUE_LENGTH = 2_000;

const TECHNICAL_JSON_KEYS = new Set([
  'id', 'url', 'href', 'src', 'style', 'class', 'classname', 'component',
  'module', 'traceid', 'requestid', 'spm', 'timestamp', 'version', 'width',
  'height', 'x', 'y', 'index', 'count', 'code', 'status', 'success',
]);

export function extractDetailFacts(documentSnapshot, detailUrl, options = {}) {
  const candidates = [];
  collectMetaFacts(documentSnapshot, candidates);
  collectStructuredElementFacts(documentSnapshot, candidates);
  collectEmbeddedJsonFacts(documentSnapshot, candidates);

  const facts = normalizeFactCandidates(candidates, options.maxFacts);
  return {
    detailUrl,
    capturedAt: new Date().toISOString(),
    pageTitle: cleanText(documentSnapshot?.title) || null,
    facts,
    diagnostics: {
      candidateCount: candidates.length,
      outputCount: facts.length,
      sources: countSources(facts),
      maxFacts: normalizeMaxFacts(options.maxFacts),
    },
  };
}

export function normalizeFactCandidates(candidates, requestedMaxFacts = DEFAULT_MAX_FACTS) {
  const maxFacts = normalizeMaxFacts(requestedMaxFacts);
  const result = [];
  const seen = new Set();

  for (const candidate of Array.isArray(candidates) ? candidates : []) {
    const label = cleanText(candidate?.label);
    const value = cleanText(candidate?.value);
    if (!isUsablePair(label, value)) continue;

    const key = `${normalizeForIdentity(label)}\u0000${normalizeForIdentity(value)}`;
    if (seen.has(key)) continue;
    seen.add(key);
    result.push({
      label: truncate(label, MAX_LABEL_LENGTH),
      value: truncate(value, MAX_VALUE_LENGTH),
      source: cleanText(candidate?.source) || 'dom',
    });
    if (result.length >= maxFacts) break;
  }

  return result;
}

function collectMetaFacts(documentSnapshot, candidates) {
  const title = cleanText(documentSnapshot?.title);
  if (title) candidates.push({ label: '商品名称', value: title, source: 'document.title' });

  for (const meta of documentSnapshot?.querySelectorAll?.('meta[name][content], meta[property][content]') ?? []) {
    const label = meta.getAttribute('name') || meta.getAttribute('property');
    const value = meta.getAttribute('content');
    if (label && value) candidates.push({ label, value, source: 'meta' });
  }
}

function collectStructuredElementFacts(documentSnapshot, candidates) {
  const selectors = [
    'table tr',
    'dl',
    '[class*="attribute"] li',
    '[class*="attribute"] [class*="item"]',
    '[class*="property"] [class*="item"]',
    '[class*="parameter"] [class*="item"]',
    '[class*="detail"] [class*="attr"]',
  ].join(',');

  for (const element of documentSnapshot?.querySelectorAll?.(selectors) ?? []) {
    const pair = readSemanticPair(element) || readLabeledChildPair(element) || readColonPair(element);
    if (pair) candidates.push({ ...pair, source: 'dom-pair' });
  }
}

function readSemanticPair(element) {
  const cells = element.querySelectorAll?.(':scope > th, :scope > td') ?? [];
  if (cells.length >= 2) {
    return { label: cells[0].textContent, value: cells[cells.length - 1].textContent };
  }

  const term = element.querySelector?.(':scope > dt');
  const definition = element.querySelector?.(':scope > dd');
  return term && definition
    ? { label: term.textContent, value: definition.textContent }
    : null;
}

function readLabeledChildPair(element) {
  const label = element.querySelector?.(
    ':scope > [class*="name"], :scope > [class*="label"], :scope > [class*="key"], :scope > dt, :scope > th',
  );
  if (!label) return null;

  const children = Array.from(element.children ?? []);
  const values = children.filter((child) => child !== label);
  if (!values.length) return null;
  return {
    label: label.textContent,
    value: values.map((child) => child.textContent).join(' '),
  };
}

function readColonPair(element) {
  const text = cleanText(element?.textContent);
  if (!text || text.length > MAX_VALUE_LENGTH + MAX_LABEL_LENGTH) return null;
  const match = text.match(/^([^：:]{1,120})[：:]\s*(.{1,2000})$/s);
  return match ? { label: match[1], value: match[2] } : null;
}

function collectEmbeddedJsonFacts(documentSnapshot, candidates) {
  const scripts = documentSnapshot?.querySelectorAll?.('script:not([src])') ?? [];
  for (const script of scripts) {
    const text = String(script.textContent ?? '').trim();
    if (!text || text.length > 2_000_000) continue;
    for (const value of extractJsonValuesFromScriptText(text)) {
      walkJson(value, candidates, 'embedded-json', 0);
    }
  }
}

export function extractJsonValuesFromScriptText(text) {
  const source = String(text ?? '').trim();
  if (!source || source.length > 2_000_000) return [];

  try {
    return [JSON.parse(source)];
  } catch {
    // 继续识别 window.__DATA__ = {...} 等静态赋值，不执行脚本。
  }

  const values = [];
  const assignmentPattern = /=\s*([{[])/g;
  for (const match of source.matchAll(assignmentPattern)) {
    if (values.length >= 20) break;
    const start = Number(match.index) + match[0].lastIndexOf(match[1]);
    const jsonText = readBalancedJson(source, start);
    if (!jsonText) continue;
    try {
      values.push(JSON.parse(jsonText));
    } catch {
      // JavaScript对象字面量不等同于JSON，拒绝宽松执行或eval。
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
    if (character === '"') {
      quote = character;
      continue;
    }
    if (character === opening) depth += 1;
    else if (character === closing) {
      depth -= 1;
      if (depth === 0) return source.slice(start, index + 1);
    }
  }
  return null;
}

function walkJson(value, candidates, source, depth) {
  if (depth > 12 || candidates.length >= DEFAULT_MAX_FACTS * 4 || value == null) return;
  if (Array.isArray(value)) {
    for (const item of value) walkJson(item, candidates, source, depth + 1);
    return;
  }
  if (typeof value !== 'object') return;

  for (const [key, child] of Object.entries(value)) {
    if (child == null) continue;
    if (['string', 'number', 'boolean'].includes(typeof child) && isUsefulJsonKey(key)) {
      candidates.push({ label: key, value: String(child), source });
    } else if (typeof child === 'object') {
      walkJson(child, candidates, source, depth + 1);
    }
  }
}

function isUsefulJsonKey(value) {
  const key = cleanText(value);
  if (!key || key.length > MAX_LABEL_LENGTH) return false;
  const normalized = normalizeForIdentity(key);
  return normalized.length >= 2 && !TECHNICAL_JSON_KEYS.has(normalized);
}

function isUsablePair(label, value) {
  if (!label || !value || label === value) return false;
  if (label.length > MAX_LABEL_LENGTH || value.length > MAX_VALUE_LENGTH) return false;
  if (/^(https?:)?\/\//i.test(label)) return false;
  return normalizeForIdentity(label).length >= 2;
}

function cleanText(value) {
  return String(value ?? '').replace(/\s+/g, ' ').trim();
}

function normalizeForIdentity(value) {
  return cleanText(value).normalize('NFKC').toLocaleLowerCase();
}

function truncate(value, maxLength) {
  return value.length <= maxLength ? value : `${value.slice(0, maxLength - 1)}…`;
}

function normalizeMaxFacts(value) {
  const normalized = Number(value);
  if (!Number.isInteger(normalized)) return DEFAULT_MAX_FACTS;
  return Math.min(2_000, Math.max(1, normalized));
}

function countSources(facts) {
  return facts.reduce((result, fact) => {
    result[fact.source] = (result[fact.source] ?? 0) + 1;
    return result;
  }, {});
}
