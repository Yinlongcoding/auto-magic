const SKIPPED_TAGS = new Set([
  'script',
  'style',
  'link',
  'meta',
  'noscript',
  'template',
  'svg',
  'path',
]);

export function serializeDomTree(root, options = {}) {
  const limits = {
    maxDepth: clampInteger(options.maxDepth, 1, 30, 12),
    maxNodes: clampInteger(options.maxNodes, 1, 10_000, 2_000),
    maxAttributes: clampInteger(options.maxAttributes, 0, 30, 10),
    maxTextLength: clampInteger(options.maxTextLength, 0, 2_000, 200),
    maxAttributeValueLength: clampInteger(
      options.maxAttributeValueLength,
      0,
      2_000,
      160,
    ),
  };
  const context = { nodeCount: 0, truncated: false, limits };
  const tree = serializeElement(root, 0, context);
  return {
    tree,
    nodeCount: context.nodeCount,
    truncated: context.truncated,
    limits,
  };
}

function serializeElement(element, depth, context) {
  if (!element?.tagName || context.nodeCount >= context.limits.maxNodes) {
    context.truncated = true;
    return null;
  }

  context.nodeCount += 1;
  const tag = String(element.tagName).toLowerCase();
  const snapshot = { tag };
  const attributes = serializeAttributes(element.attributes, context.limits);
  if (Object.keys(attributes).length) snapshot.attributes = attributes;

  const text = readDirectText(element, context.limits.maxTextLength);
  if (text) snapshot.text = text;

  const children = Array.from(element.children ?? []).filter(
    (child) => !SKIPPED_TAGS.has(String(child.tagName ?? '').toLowerCase()),
  );
  if (!children.length) return snapshot;

  if (depth >= context.limits.maxDepth) {
    snapshot.childrenTruncated = children.length;
    context.truncated = true;
    return snapshot;
  }

  const serializedChildren = [];
  for (const child of children) {
    if (context.nodeCount >= context.limits.maxNodes) {
      context.truncated = true;
      break;
    }
    const serialized = serializeElement(child, depth + 1, context);
    if (serialized) serializedChildren.push(serialized);
  }
  if (serializedChildren.length) snapshot.children = serializedChildren;
  if (serializedChildren.length < children.length) {
    snapshot.childrenTruncated = children.length - serializedChildren.length;
  }
  return snapshot;
}

function serializeAttributes(attributes, limits) {
  const result = {};
  const safeAttributes = Array.from(attributes ?? []).filter(({ name = '' }) => {
    const normalized = String(name).toLowerCase();
    return !normalized.startsWith('on') && isUsefulAttribute(normalized);
  });

  for (const attribute of safeAttributes.slice(0, limits.maxAttributes)) {
    result[attribute.name] = truncate(attribute.value, limits.maxAttributeValueLength);
  }
  return result;
}

function isUsefulAttribute(name) {
  return (
    ['id', 'class', 'name', 'role', 'itemprop', 'property', 'content'].includes(name) ||
    name.startsWith('data-') ||
    name.startsWith('aria-')
  );
}

function readDirectText(element, maxLength) {
  if (!maxLength) return null;
  const value = Array.from(element.childNodes ?? [])
    .filter((node) => Number(node.nodeType) === 3)
    .map((node) => node.textContent ?? '')
    .join(' ')
    .replace(/\s+/g, ' ')
    .trim();
  return value ? truncate(value, maxLength) : null;
}

function truncate(value, maxLength) {
  const normalized = String(value ?? '');
  if (normalized.length <= maxLength) return normalized;
  return `${normalized.slice(0, Math.max(0, maxLength - 1))}…`;
}

function clampInteger(value, minimum, maximum, fallback) {
  const normalized = Number(value);
  if (!Number.isInteger(normalized)) return fallback;
  return Math.min(maximum, Math.max(minimum, normalized));
}
