import { test } from 'node:test';
import assert from 'node:assert/strict';
import { createServer } from 'node:http';
import { createDraftText, validateText } from './draft-text.mjs';

const ingressKey = 'test-only-draft-ingress-key-32-characters';
const gatewayToken = 'test-only-native-gateway-token-32-characters';
const valid = { model: 'openclaw/default', messages: [{ role: 'system', content: 'Text only' }, { role: 'user', content: 'Xin chào' }], max_tokens: 800, temperature: 0.2 };

test('rejects model overrides, tools, media and non-text messages', () => {
  assert.ok(validateText(valid));
  for (const change of [{ model: 'other' }, { tools: [] }, { user: 'same-session' }, { stream: true }, { messages: [{ role: 'user', content: [{ type: 'image_url' }] }] }, { messages: [{ role: 'tool', content: 'x' }] }]) assert.equal(validateText({ ...valid, ...change }), null);
});

test('text proxy uses distinct keys, validates payload, sends only permitted data and requires approval', async t => {
  let calls = 0;
  const text = createDraftText({ ingressKey, gatewayToken, gatewayPort: 18789, approved: true, fetchImpl: async (url, options) => {
    calls++;
    assert.equal(url, 'http://127.0.0.1:18789/v1/chat/completions');
    assert.equal(options.headers.authorization, 'Bearer ' + gatewayToken);
    assert.deepEqual(JSON.parse(options.body), { ...valid, stream: false });
    assert.equal(options.headers['x-openclaw-model'], undefined);
    return Response.json({ model: 'openclaw/default', choices: [{ message: { content: 'Xin chào!', role: 'assistant' }, finish_reason: 'stop' }] });
  } });
  assert.equal(createDraftText({ ingressKey, gatewayToken, approved: false }).enabled, false);
  assert.equal(createDraftText({ ingressKey: gatewayToken, gatewayToken, approved: true }).enabled, false);
  const server = createServer((req, res) => void text.handle(req, res));
  await new Promise(resolve => server.listen(0, '127.0.0.1', resolve));
  t.after(() => server.close());
  const url = `http://127.0.0.1:${server.address().port}/v1/chat/completions`;
  const send = (key, body, extra = {}) => fetch(url, { method: 'POST', headers: { authorization: 'Bearer ' + key, 'content-type': 'application/json', ...extra }, body: JSON.stringify(body) });
  assert.equal((await send(gatewayToken, valid)).status, 401);
  assert.equal((await send(ingressKey, { ...valid, tools: [] })).status, 400);
  assert.equal((await send(ingressKey, { ...valid, messages: [{ role: 'user', content: 'x'.repeat(140000) }] })).status, 413);
  const response = await send(ingressKey, valid, { 'x-openclaw-model': 'unapproved-model' });
  assert.equal(response.status, 200);
  assert.equal((await response.json()).choices[0].message.content, 'Xin chào!');
  assert.equal(calls, 1);
});
