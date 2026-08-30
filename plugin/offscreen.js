import { serializeDomTree } from './lib/dom-snapshot.js';

const activeObjectUrls = new Set();

chrome.runtime.onMessage.addListener((message, _sender, sendResponse) => {
  if (message?.target !== 'offscreen') return false;

  if (message.type === 'PARSE_1688_DETAIL_DOM') {
    void parseDetailDom(message)
      .then((data) => sendResponse({ success: true, data }))
      .catch((error) => sendResponse({ success: false, error: error.message }));
    return true;
  }

  if (message.type === 'REVOKE_DETAIL_DOM_DOWNLOAD_URL') {
    revokeObjectUrl(message.url);
    sendResponse({ success: true });
    return false;
  }

  return false;
});

async function parseDetailDom(message) {
  const requestedUrl = normalizeAllowed1688Url(message.detailUrl);
  const maxHtmlBytes = normalizeLimit(message.options?.maxHtmlBytes, 1_000, 10_000_000);
  const response = await fetch(requestedUrl, {
    cache: 'no-store',
    credentials: 'include',
    redirect: 'follow',
  });
  if (!response.ok) throw new Error(`详情页请求失败：HTTP ${response.status}`);

  const finalUrl = normalizeAllowed1688Url(response.url);
  const contentType = response.headers.get('content-type') || '';
  if (!contentType.toLowerCase().includes('html')) {
    throw new Error(`详情页返回的不是HTML：${contentType || '未知类型'}`);
  }

  const html = await readResponseText(response, maxHtmlBytes);
  const documentSnapshot = new DOMParser().parseFromString(html, 'text/html');
  const structure = serializeDomTree(documentSnapshot.documentElement, message.options);
  const capturedAt = new Date().toISOString();
  const exportText = [
    'Auto Magic 1688 Detail DOM',
    `Requested URL: ${requestedUrl}`,
    `Final URL: ${finalUrl}`,
    `Captured At: ${capturedAt}`,
    '',
    '----- RAW HTML (stored as text; scripts are not executed) -----',
    html,
  ].join('\n');
  const downloadUrl = URL.createObjectURL(
    new Blob([exportText], { type: 'text/plain;charset=utf-8' }),
  );
  activeObjectUrls.add(downloadUrl);

  return {
    requestedUrl,
    finalUrl,
    capturedAt,
    pageTitle: documentSnapshot.title || null,
    contentType,
    htmlBytes: new TextEncoder().encode(html).byteLength,
    structure,
    downloadUrl,
  };
}

async function readResponseText(response, maxBytes) {
  const declaredLength = Number(response.headers.get('content-length') || 0);
  if (declaredLength > maxBytes) {
    throw new Error(`详情页HTML超过大小限制：${declaredLength} bytes`);
  }

  if (!response.body) {
    const text = await response.text();
    if (new TextEncoder().encode(text).byteLength > maxBytes) {
      throw new Error(`详情页HTML超过大小限制：${maxBytes} bytes`);
    }
    return text;
  }

  const reader = response.body.getReader();
  const decoder = new TextDecoder();
  const chunks = [];
  let receivedBytes = 0;
  while (true) {
    const { done, value } = await reader.read();
    if (done) break;
    receivedBytes += value.byteLength;
    if (receivedBytes > maxBytes) {
      await reader.cancel();
      throw new Error(`详情页HTML超过大小限制：${maxBytes} bytes`);
    }
    chunks.push(decoder.decode(value, { stream: true }));
  }
  chunks.push(decoder.decode());
  return chunks.join('');
}

function normalizeAllowed1688Url(value) {
  const url = new URL(value);
  if (url.protocol !== 'https:') throw new Error('详情页只允许使用HTTPS。');
  if (url.hostname !== '1688.com' && !url.hostname.endsWith('.1688.com')) {
    throw new Error('详情页只允许访问1688.com及其子域名。');
  }
  return url.href;
}

function normalizeLimit(value, minimum, maximum) {
  const normalized = Number(value);
  if (!Number.isInteger(normalized)) return maximum;
  return Math.min(maximum, Math.max(minimum, normalized));
}

function revokeObjectUrl(value) {
  if (!activeObjectUrls.has(value)) return;
  URL.revokeObjectURL(value);
  activeObjectUrls.delete(value);
}
