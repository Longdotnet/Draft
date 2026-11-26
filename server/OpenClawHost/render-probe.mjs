import { spawn } from 'node:child_process';
import { randomBytes } from 'node:crypto';
import { mkdtempSync, writeFileSync } from 'node:fs';
import { createServer } from 'node:http';
import { tmpdir } from 'node:os';
import { join } from 'node:path';
import { createOwnerUi } from './owner-ui.mjs';
import { createDraftText } from './draft-text.mjs';

// Isolated owner-managed text pilot. Free Render state is ephemeral.
const state = mkdtempSync(join(tmpdir(), 'openclaw-probe-'));
const config = join(state, 'openclaw.json');
const gatewayPort = 18789;
const publicOrigin = 'https://draft-openclaw-probe.onrender.com';
const ownerPassword = process.env.OPENCLAW_OWNER_PASSWORD;
const ownerEnabled = typeof ownerPassword === 'string' && ownerPassword.length >= 32;
const gatewayToken = randomBytes(32).toString('hex');
const ingressKey = process.env.OPENCLAW_DRAFT_TEXT_KEY;
const text = createDraftText({ ingressKey, gatewayToken, gatewayPort,
  approved: ownerEnabled && ingressKey !== ownerPassword && process.env.OPENCLAW_PLAN_ACCESS_APPROVED === 'true' });
writeFileSync(config, JSON.stringify({
  gateway: {
    mode: 'local', bind: 'loopback', port: gatewayPort,
    auth: { mode: 'token' },
    ...(ownerEnabled ? { publicOrigin } : {}),
    controlUi: { enabled: ownerEnabled, allowedOrigins: [publicOrigin] }, terminal: { enabled: false },
    nodes: { pairing: { autoApproveLocal: false } },
    uploads: { enabled: false },
    http: { endpoints: { chatCompletions: { enabled: text.enabled } } },
  },
  agents: { defaults: { skipBootstrap: true, contextInjection: 'never', maxConcurrent: 1 } },
  tools: { deny: ['*'] },
}), { mode: 0o600 });

const gatewayEnv = { PATH: process.env.PATH, HOME: process.env.HOME, NODE_ENV: 'production',
    OPENCLAW_STATE_DIR: state, OPENCLAW_CONFIG_PATH: config,
    OPENCLAW_GATEWAY_TOKEN: gatewayToken };
const owner = ownerEnabled ? createOwnerUi({ password: ownerPassword, publicOrigin, gatewayPort, gatewayEnv }) : null;
const child = spawn(process.execPath, ['/app/openclaw.mjs', 'gateway'], {
  env: gatewayEnv,
  stdio: ['ignore', 'inherit', 'inherit'],
});
const server = createServer(async (req, res) => {
  if (req.url === '/v1/chat/completions') return text.handle(req, res);
  if (req.method !== 'GET' || !['/healthz', '/startupz', '/readyz'].includes(req.url)) {
    if (owner) {
      try { return await owner.handle(req, res); } catch { if (!res.headersSent) res.writeHead(500).end('Request failed'); else res.destroy(); return; }
    }
    if (req.method === 'GET' && req.url === '/') return res.writeHead(503, { 'content-type': 'text/plain; charset=utf-8', 'cache-control': 'no-store' }).end('OpenClaw owner UI disabled. Set OPENCLAW_OWNER_PASSWORD in Render Environment.');
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
