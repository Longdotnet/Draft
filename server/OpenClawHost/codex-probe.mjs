import { spawn } from 'node:child_process';
import { mkdtempSync, readFileSync, rmSync } from 'node:fs';
import { createServer } from 'node:http';
import { tmpdir } from 'node:os';
import { join } from 'node:path';
import { createInterface } from 'node:readline';

// Isolated protocol/capacity check. No login, credentials, agent turns, or shell RPCs.
const home = mkdtempSync(join(tmpdir(), 'draft-codex-probe-'));
const startedAt = performance.now();
const smoke = process.argv.includes('--smoke');
const env = Object.fromEntries(['PATH', 'Path', 'SystemRoot', 'WINDIR', 'ComSpec', 'TEMP', 'TMP', 'TMPDIR', 'LANG'].filter(k => process.env[k]).map(k => [k, process.env[k]]));
Object.assign(env, { HOME: home, USERPROFILE: home, CODEX_HOME: home });
const binary = process.env.CODEX_PROBE_BINARY || 'codex';
const child = spawn(binary, ['app-server', '--listen', 'stdio://'], { cwd: home, env, stdio: ['pipe', 'pipe', 'pipe'], windowsHide: true });
let ready = false;
let startupMs = null;
let stopping = false;
let server;
let result = 1;
const metrics = () => {
  const read = path => { try { const value = Number(readFileSync(path, 'utf8').trim()); return Number.isFinite(value) ? Math.round(value / 1048576 * 10) / 10 : null; } catch { return null; } };
  return { cgroupMemoryMiB: read('/sys/fs/cgroup/memory.current'), cgroupPeakMiB: read('/sys/fs/cgroup/memory.peak'), cgroupLimitMiB: read('/sys/fs/cgroup/memory.max') };
};
const status = () => ({ runtime: 'Codex App Server', ready, startupMs, ...metrics(), chatGptConnected: false, inferenceEnabled: false });
const send = value => child.stdin.write(JSON.stringify(value) + '\n');
function stop(code) {
  if (stopping) return;
  stopping = true;
  result = code;
  ready = false;
  clearTimeout(deadline);
  server?.close();
  child.stdin.end();
  child.kill();
  const force = setTimeout(() => { child.kill('SIGKILL'); process.exit(result); }, 2000);
  force.unref();
}
const deadline = setTimeout(() => { console.error('Codex initialization exceeded 30-second runtime budget'); stop(1); }, 30_000);
child.stderr.resume(); // Do not print arbitrary upstream output to public hosting logs.
child.stdin.on('error', () => stop(1));
child.on('error', () => { console.error('Codex process could not start'); stop(1); });
child.on('close', () => {
  clearTimeout(deadline);
  server?.close();
  try { rmSync(home, { recursive: true, force: true }); } catch { /* ephemeral probe state */ }
  process.exit(stopping ? result : 1);
});
const lines = createInterface({ input: child.stdout });
lines.on('line', line => {
  let message;
  try { message = JSON.parse(line); } catch { return; }
  if (message.id !== 1 || ready || stopping) return;
  if (message.error || !message.result) { console.error('Codex initialize RPC rejected'); stop(1); return; }
  send({ method: 'initialized', params: {} });
  startupMs = Math.round(performance.now() - startedAt);
  clearTimeout(deadline);
  ready = true;
  console.log(JSON.stringify(status()));
  if (smoke) stop(0);
});
send({ id: 1, method: 'initialize', params: { clientInfo: { name: 'draft_runtime_probe', title: 'Draft Runtime Probe', version: '1.0.0' } } });

if (!smoke) {
  server = createServer((req, res) => {
    if (req.method !== 'GET') return res.writeHead(405).end();
    if (['/healthz', '/readyz', '/startupz'].includes(req.url)) {
      return res.writeHead(ready ? 200 : 503, { 'content-type': 'application/json', 'cache-control': 'no-store' }).end(JSON.stringify(status()));
    }
    if (req.url === '/') {
      return res.writeHead(ready ? 200 : 503, { 'content-type': 'text/html; charset=utf-8', 'cache-control': 'no-store', 'content-security-policy': "default-src 'none'; style-src 'unsafe-inline'; frame-ancestors 'none'" }).end(`<!doctype html><html lang="vi"><meta charset="utf-8"><meta name="viewport" content="width=device-width,initial-scale=1"><title>Draft · Codex Runtime</title><style>body{font:16px system-ui;max-width:640px;margin:10vh auto;padding:20px}code{background:#eee;padding:4px}</style><h1>Draft · Codex Runtime</h1><p>Runtime: ${ready ? 'Sẵn sàng' : 'Đang khởi động'}</p><p>Thời gian initialize: ${startupMs ?? '—'} ms</p><p>Chưa kết nối tài khoản ChatGPT. Chưa bật inference.</p><p>Chỉ kích hoạt tích hợp ứng dụng cloud khi có quyền ChatGPT plan usage phù hợp. Không yêu cầu nhập mật khẩu hoặc token tại trang này.</p><p><a href="/readyz">Kiểm tra runtime thật</a> · <a href="https://developers.openai.com/siwc/token-sharing-open-source/codex-app-server">Tài liệu OpenAI</a></p></html>`);
    }
    res.writeHead(404).end();
  });
  server.listen(Number(process.env.PORT || 10000), '0.0.0.0');
}
for (const signal of ['SIGTERM', 'SIGINT']) process.on(signal, () => stop(0));
