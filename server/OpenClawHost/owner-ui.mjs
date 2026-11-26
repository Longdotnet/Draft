import { execFile } from 'node:child_process';
import { createHash, randomBytes, timingSafeEqual } from 'node:crypto';
import { request } from 'node:http';
import { promisify } from 'node:util';

const run = promisify(execFile);
const cookieName = '__Host-draft-claw';
const sessionTtl = 8 * 60 * 60 * 1000;
const ownerPath = '/_draft/owner';
const digest = value => createHash('sha256').update(value).digest();
const page = (title, content) => `<!doctype html><html lang="vi"><meta charset="utf-8"><meta name="viewport" content="width=device-width,initial-scale=1"><title>${title}</title><style>body{font:16px system-ui;max-width:520px;margin:8vh auto;padding:20px;color:#17212b}input,button{box-sizing:border-box;width:100%;padding:13px;margin:12px 0;font:inherit}button{cursor:pointer}small{color:#58616b}</style><h1>${title}</h1>${content}</html>`;

export function createOwnerUi({ password, publicOrigin, gatewayPort, gatewayEnv, dashboard = issueDashboard }) {
  if (!password || password.length < 32) throw new Error('Owner password must be at least 32 characters');
  const origin = new URL(publicOrigin);
  if (origin.protocol !== 'https:' || origin.origin !== publicOrigin) throw new Error('An exact HTTPS origin is required');
  const passwordHash = digest(password);
  const sessions = new Map();
  const connections = new Set();
  const revoke = () => { sessions.clear(); for (const socket of connections) socket.destroy(); connections.clear(); };
  let nextLogin = 0;
  let pairing = false;
  const isOwner = req => {
    const token = String(req.headers.cookie || '').split(';').map(x => x.trim()).find(x => x.startsWith(cookieName + '='))?.slice(cookieName.length + 1);
    if (!token) return false;
    const expiry = sessions.get(token);
    if (!expiry || expiry <= Date.now()) { sessions.delete(token); return false; }
    return true;
  };
  const sameOrigin = req => req.headers.origin === origin.origin;
  const send = (res, status, body, headers = {}) => {
    res.writeHead(status, { 'content-type': 'text/html; charset=utf-8', 'cache-control': 'no-store', 'referrer-policy': 'no-referrer', 'x-content-type-options': 'nosniff', 'x-frame-options': 'DENY', 'content-security-policy': "default-src 'none'; style-src 'unsafe-inline'; form-action 'self'; frame-ancestors 'none'; base-uri 'none'", ...headers });
    res.end(body);
  };
  const loginPage = () => page('Draft OpenClaw — quản trị', '<p>Nhập mật khẩu quản trị riêng của service. Không nhập mật khẩu ChatGPT tại đây.</p><form method="post" action="/_draft/login"><label>Mật khẩu quản trị<input type="password" name="password" required autocomplete="current-password"></label><button>Đăng nhập</button></form>');
  const ownerPage = () => page('Kết nối OpenClaw', '<p>Mở Control UI bằng mã ghép cặp chính thức, chỉ dùng một lần.</p><p><small>Render Free: dữ liệu trên ổ đĩa tạm có thể mất khi restart/deploy. Chỉ thử đăng nhập sau khi kiểm chứng lưu trữ bền vững. API cho Draft chưa bật.</small></p><form method="post" action="/_draft/pair"><button>Mở Control UI</button></form><form method="post" action="/_draft/logout"><button>Đăng xuất</button></form>');
  const headersFor = (req, upgrade = false) => {
    const headers = { ...req.headers, host: origin.host, 'x-forwarded-proto': 'https' };
    // Never pass the owner session or trust inbound proxy identity headers.
    for (const key of Object.keys(headers)) if (key === 'cookie' || key === 'forwarded' || key.startsWith('x-forwarded-') || key.startsWith('tailscale-')) delete headers[key];
    headers['x-forwarded-proto'] = 'https';
    headers['x-forwarded-host'] = origin.host;
    if (!upgrade) { delete headers.connection; delete headers.upgrade; }
    return headers;
  };
  const blocked = path => path.startsWith('/v1/') || path.startsWith('/tools/') || path.startsWith('/_draft/');
  async function handle(req, res) {
    let path;
    try { path = new URL(req.url, origin).pathname; } catch { return send(res, 400, 'Bad request'); }
    if (req.method === 'POST' && path === '/_draft/login') {
      if (!sameOrigin(req)) return send(res, 403, 'Forbidden');
      if (Date.now() < nextLogin) return send(res, 429, 'Thử lại sau.');
      nextLogin = Date.now() + 2000;
      let body = '';
      for await (const chunk of req) { body += chunk; if (body.length > 4096) return send(res, 413, 'Too large'); }
      const supplied = new URLSearchParams(body).get('password') || '';
      if (!timingSafeEqual(digest(supplied), passwordHash)) return send(res, 401, loginPage());
      // One active owner browser session; no unbounded session store.
      revoke();
      const token = randomBytes(32).toString('hex');
      sessions.set(token, Date.now() + sessionTtl);
      return send(res, 303, '', { location: ownerPath, 'set-cookie': `${cookieName}=${token}; Secure; HttpOnly; SameSite=Strict; Path=/; Max-Age=${sessionTtl / 1000}` });
    }
    if (!isOwner(req)) return send(res, req.method === 'GET' && path === '/' ? 200 : 401, loginPage());
    if (req.method !== 'GET' && req.method !== 'HEAD' && !sameOrigin(req)) return send(res, 403, 'Forbidden');
    if (path === ownerPath && req.method === 'GET') return send(res, 200, ownerPage());
    if (path === '/_draft/logout' && req.method === 'POST') {
      revoke();
      return send(res, 303, '', { location: '/', 'set-cookie': `${cookieName}=; Secure; HttpOnly; SameSite=Strict; Path=/; Max-Age=0` });
    }
    if (path === '/_draft/pair' && req.method === 'POST') {
      if (pairing) return send(res, 409, 'Pairing already in progress');
      pairing = true;
      try {
        const link = await dashboard(gatewayEnv);
        if (!isOwner(req)) return send(res, 401, 'Session expired');
        const local = new URL(link);
        const fragment = new URLSearchParams(local.hash.slice(1));
        if (!fragment.get('bootstrapToken') || !fragment.get('gatewayUrl')) throw new Error('Invalid dashboard handoff');
        fragment.set('gatewayUrl', 'wss://' + origin.host);
        return send(res, 303, '', { location: origin.origin + '/#' + fragment.toString() });
      } catch { return send(res, 503, page('OpenClaw chưa sẵn sàng', '<p>Không tạo được liên kết ghép cặp. Kiểm tra trạng thái runtime trên Render.</p><a href="/_draft/owner">Quay lại</a>')); }
      finally { pairing = false; }
    }
    if (blocked(path)) return send(res, 404, 'Not found');
    const upstream = request({ hostname: '127.0.0.1', port: gatewayPort, path: req.url, method: req.method, headers: headersFor(req) }, incoming => {
      res.writeHead(incoming.statusCode || 502, { ...incoming.headers, 'cache-control': 'no-store', 'referrer-policy': 'no-referrer' });
      incoming.pipe(res);
    });
    upstream.setTimeout(60_000, () => upstream.destroy());
    upstream.on('error', () => { if (!res.headersSent) send(res, 503, 'Gateway unavailable'); else res.destroy(); });
    req.pipe(upstream);
  }
  function upgrade(req, socket, head) {
    if (!isOwner(req) || !sameOrigin(req) || req.url !== '/') { socket.end('HTTP/1.1 403 Forbidden\r\nConnection: close\r\n\r\n'); return; }
    const upstream = request({ hostname: '127.0.0.1', port: gatewayPort, path: '/', method: 'GET', headers: headersFor(req, true) });
    upstream.on('upgrade', (response, peer, remainder) => {
      if (!isOwner(req)) { peer.destroy(); socket.destroy(); return; }
      connections.add(socket);
      const expiry = setTimeout(() => socket.destroy(), sessionTtl);
      expiry.unref();
      socket.once('close', () => { connections.delete(socket); clearTimeout(expiry); peer.destroy(); });
      peer.once('close', () => socket.destroy());
      socket.write('HTTP/1.1 101 Switching Protocols\r\n' + response.rawHeaders.reduce((result, value, index) => result + (index % 2 ? value + '\r\n' : value + ': '), '') + '\r\n');
      if (head.length) peer.write(head);
      if (remainder.length) socket.write(remainder);
      socket.on('error', () => peer.destroy()); peer.on('error', () => socket.destroy());
      socket.pipe(peer).pipe(socket);
    });
    upstream.on('response', response => { response.resume(); socket.destroy(); });
    upstream.on('error', () => socket.destroy());
    upstream.end();
  }
  return { handle, upgrade };
}

async function issueDashboard(env) {
  const { stdout } = await run(process.execPath, ['/app/openclaw.mjs', 'dashboard', '--json'], { env, timeout: 60_000, maxBuffer: 128 * 1024 });
  // Never return or log the legacy shared-token URL or other CLI credentials.
  return JSON.parse(stdout).browserUrl;
}
