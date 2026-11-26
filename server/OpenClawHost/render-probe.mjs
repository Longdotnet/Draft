import { spawn } from 'node:child_process';
import { randomBytes } from 'node:crypto';
import { mkdtempSync, writeFileSync } from 'node:fs';
import { createServer } from 'node:http';
import { tmpdir } from 'node:os';
import { join } from 'node:path';
import { createOwnerUi } from './owner-ui.mjs';

// Capacity probe only: no OAuth, channels, tools or publicly exposed AI endpoint.
const state = mkdtempSync(join(tmpdir(), 'openclaw-probe-'));
const config = join(state, 'openclaw.json');
const gatewayPort = 18789;
const publicOrigin = 'https://draft-openclaw-probe.onrender.com';
const ownerPassword = process.env.OPENCLAW_OWNER_PASSWORD;
const ownerEnabled = typeof ownerPassword === 'string' && ownerPassword.length >= 32;
writeFileSync(config, JSON.stringify({
  gateway: {
    mode: 'local', bind: 'loopback', port: gatewayPort,
    ...(ownerEnabled ? { publicOrigin } : {}),
    auth: { mode: 'token' },
    controlUi: { enabled: ownerEnabled, allowedOrigins: [publicOrigin] }, terminal: { enabled: false },
    nodes: { pairing: { autoApproveLocal: false } },
    uploads: { enabled: false },
    http: { endpoints: { chatCompletions: { enabled: false } } },
  },
  agents: { defaults: { skipBootstrap: true, contextInjection: 'never', maxConcurrent: 1 } },
  plugins: { allow: ['openai'], entries: { openai: { enabled: true } } },
  update: { checkOnStart: false },
  tools: { deny: ['*'] },
}), { mode: 0o600 });

const gatewayEnv = { PATH: process.env.PATH, HOME: process.env.HOME, NODE_ENV: 'production',
    OPENCLAW_STATE_DIR: state, OPENCLAW_CONFIG_PATH: config,
    OPENCLAW_GATEWAY_TOKEN: randomBytes(32).toString('hex'), OPENCLAW_NO_AUTO_UPDATE: '1' };
const owner = ownerEnabled ? createOwnerUi({ password: ownerPassword, publicOrigin, gatewayPort, gatewayEnv }) : null;
const child = spawn(process.execPath, ['/app/openclaw.mjs', 'gateway'], {
  env: gatewayEnv,
  stdio: ['ignore', 'inherit', 'inherit'],
});
const server = createServer(async (req, res) => {
  if (req.method !== 'GET' || !['/healthz', '/startupz', '/readyz'].includes(req.url)) {
    if (owner) {
      try { await owner.handle(req, res); } catch { if (!res.headersSent) res.writeHead(500).end('Request failed'); else res.destroy(); }
      return;
    }
    if (req.method === 'GET' && req.url === '/') {
      res.writeHead(503, { 'content-type': 'text/plain; charset=utf-8', 'cache-control': 'no-store' }).end('OpenClaw owner UI disabled. Configure OPENCLAW_OWNER_PASSWORD in Render Environment (32+ characters).');
      return;
    }
    res.writeHead(404).end();
    return;
  }
  let status = 503;
  try {
    const response = await fetch(`http://127.0.0.1:${gatewayPort}${req.url}`, { signal: AbortSignal.timeout(4000) });
    status = response.ok ? 200 : 503;
    await response.body?.cancel();
  } catch { /* Gateway is not available yet. */ }
  res.writeHead(status, { 'content-type': 'application/json', 'cache-control': 'no-store' });
  res.end(JSON.stringify({ ready: status === 200 }));
});
server.on('upgrade', (req, socket, head) => { if (owner) owner.upgrade(req, socket, head); else socket.destroy(); });
server.listen(Number(process.env.PORT || 10000), '0.0.0.0');
child.on('error', () => process.exit(1));
child.on('exit', code => process.exit(code ?? 1));
for (const signal of ['SIGTERM', 'SIGINT']) process.on(signal, () => { server.close(); child.kill(signal); });
