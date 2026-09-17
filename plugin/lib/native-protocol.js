export const NATIVE_HOST_NAME = 'com.automagic.desktop';
export const PROTOCOL_VERSION = '1.0';

export const NATIVE_MESSAGE_TYPES = Object.freeze({
  extensionReady: 'extension.ready',
  searchStart: 'search.start',
  searchAccepted: 'search.accepted',
  searchProgress: 'search.progress',
  searchCompleted: 'search.completed',
  searchFailed: 'search.failed',
  bridgeStatus: 'bridge.status',
});

export function createNativeEnvelope(type, requestId, payload = undefined, error = undefined) {
  const envelope = {
    protocolVersion: PROTOCOL_VERSION,
    requestId: String(requestId ?? ''),
    type,
  };
  if (payload !== undefined) envelope.payload = payload;
  if (error !== undefined) envelope.error = error;
  return envelope;
}

export function normalizeDesktopSearchEnvelope(message) {
  if (!message || message.protocolVersion !== PROTOCOL_VERSION) {
    throw new TypeError('桌面端与插件的协议版本不一致。');
  }
  if (message.type !== NATIVE_MESSAGE_TYPES.searchStart) {
    throw new TypeError(`不支持的桌面端消息：${message.type ?? 'unknown'}。`);
  }

  const requestId = String(message.requestId ?? '').trim();
  if (!requestId) throw new TypeError('桌面端消息缺少 requestId。');

  const keyword = String(message.payload?.keyword ?? '').trim();
  if (!keyword) throw new TypeError('搜索词不能为空。');
  if (keyword.length > 100) throw new TypeError('搜索词不能超过100个字符。');

  const requestedMaxItems = Number(message.payload?.maxItems ?? 60);
  if (!Number.isInteger(requestedMaxItems) || requestedMaxItems < 1) {
    throw new TypeError('maxItems 必须是正整数。');
  }

  return {
    requestId,
    keyword,
    maxItems: Math.min(requestedMaxItems, 60),
    procurementMinimumCny: message.payload?.procurementMinimumCny,
    procurementMaximumCny: message.payload?.procurementMaximumCny,
    sortMode: message.payload?.sortMode,
    includeDetailFacts: message.payload?.includeDetailFacts === true,
  };
}
