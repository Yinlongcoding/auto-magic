import assert from 'node:assert/strict';
import { readFile } from 'node:fs/promises';
import test from 'node:test';

const source = await readFile(
  new URL('../plugin/lib/native-protocol.js', import.meta.url),
  'utf8',
);
const protocol = await import(
  `data:text/javascript;base64,${Buffer.from(source).toString('base64')}`
);

test('normalizes a desktop search request', () => {
  const request = protocol.normalizeDesktopSearchEnvelope({
    protocolVersion: '1.0',
    requestId: 'request-1',
    type: 'search.start',
    payload: { keyword: ' 连衣裙 ', maxItems: 80 },
  });

  assert.deepEqual(request, {
    requestId: 'request-1',
    keyword: '连衣裙',
    maxItems: 60,
    procurementMinimumCny: undefined,
    procurementMaximumCny: undefined,
    sortMode: undefined,
    includeDetailFacts: false,
  });
});

test('keeps procurement range and sort mode from a desktop search request', () => {
  const request = protocol.normalizeDesktopSearchEnvelope({
    protocolVersion: '1.0',
    requestId: 'request-2',
    type: 'search.start',
    payload: {
      keyword: '连衣裙',
      maxItems: 60,
      procurementMinimumCny: 40,
      procurementMaximumCny: 64,
      sortMode: 'sales',
      includeDetailFacts: true,
    },
  });

  assert.deepEqual(request, {
    requestId: 'request-2',
    keyword: '连衣裙',
    maxItems: 60,
    procurementMinimumCny: 40,
    procurementMaximumCny: 64,
    sortMode: 'sales',
    includeDetailFacts: true,
  });
});

test('rejects unsupported protocol versions', () => {
  assert.throws(
    () => protocol.normalizeDesktopSearchEnvelope({
      protocolVersion: '2.0',
      requestId: 'request-1',
      type: 'search.start',
      payload: { keyword: '连衣裙' },
    }),
    /协议版本不一致/,
  );
});

test('creates a versioned result envelope', () => {
  assert.deepEqual(
    protocol.createNativeEnvelope('search.completed', 'request-2', { count: 60 }),
    {
      protocolVersion: '1.0',
      requestId: 'request-2',
      type: 'search.completed',
      payload: { count: 60 },
    },
  );
});

test('declares progress heartbeats for long-running detail capture', () => {
  assert.equal(protocol.NATIVE_MESSAGE_TYPES.searchProgress, 'search.progress');
});
