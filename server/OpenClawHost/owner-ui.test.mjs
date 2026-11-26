import { test } from 'node:test';
import assert from 'node:assert/strict';
import { createServer } from 'node:http';
import { createOwnerUi } from './owner-ui.mjs';

const origin = 'https://draft-openclaw-probe.onrender.com';
const password = 'test-only-32-character-owner-password';
const listen = server => new Promise(resolve => server.listen(0, '127.0.0.1', () => resolve(server.address().port)));

test('owner gate blocks unauthenticated requests and uses official one-time browser handoff', async t => {
  let calls = 0;
  const gateway = createServer((req, res) => {
    assert.equal(req.headers.cookie, undefined);
    assert.equal(req.headers.host, new URL(origin).host);
    res.end('native gateway');
  });
  const gatewayPort = await listen(gateway);
  t.after(() => gateway.close());
  const owner = createOwnerUi({ password, publicOrigin: origin, gatewayPort, gatewayEnv: {}, dashboard: async () => {
    calls++;
    return 'http://127.0.0.1:18789/#bootstrapToken=test-once&bootstrapProfile=owner&gatewayUrl=ws%3A%2F%2F127.0.0.1%3A18789';
  } });
  const server = createServer((req, res) => void owner.handle(req, res));
  server.on('upgrade', owner.upgrade);
  const port = await listen(server);
  t.after(() => server.close());
  const call = (path, options = {}) => fetch(`http://127.0.0.1:${port}${path}`, { redirect: 'manual', ...options });
  assert.equal((await call('/')).status, 200);
  assert.equal((await call('/_draft/pair', { method: 'POST', headers: { origin } })).status, 401);
  assert.equal((await call('/_draft/login', { method: 'POST', headers: { origin: 'https://wrong.test' }, body: new URLSearchParams({ password }) })).status, 403);
  const login = await call('/_draft/login', { method: 'POST', headers: { origin }, body: new URLSearchParams({ password }) });
  assert.equal(login.status, 303);
  const cookie = login.headers.get('set-cookie');
  assert.match(cookie, /Secure; HttpOnly; SameSite=Strict/);
  const headers = { cookie: cookie.split(';')[0], origin };
  assert.equal(await (await call('/', { headers })).text(), 'native gateway');
  assert.equal((await call('/v1/chat/completions', { headers })).status, 404);
  assert.equal((await call('/_draft/pair', { method: 'POST', headers: { ...headers, origin: 'https://wrong.test' } })).status, 403);
  const pair = await call('/_draft/pair', { method: 'POST', headers });
  assert.equal(pair.status, 303);
  const link = new URL(pair.headers.get('location'));
  assert.equal(link.origin, origin);
  assert.equal(new URLSearchParams(link.hash.slice(1)).get('gatewayUrl'), 'wss://' + new URL(origin).host);
  assert.equal(calls, 1);
  assert.equal((await call('/_draft/logout', { method: 'POST', headers })).status, 303);
  assert.equal((await call('/control-ui-config.json', { headers })).status, 401);
});

test('refuses weak password or insecure origin', () => {
  assert.throws(() => createOwnerUi({ password: 'short', publicOrigin: origin }), /32 characters/);
  assert.throws(() => createOwnerUi({ password, publicOrigin: 'http://example.test' }), /HTTPS/);
});
