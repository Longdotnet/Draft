import { createHash, timingSafeEqual } from 'node:crypto';

const route = '/v1/chat/completions';
const limit = 128 * 1024;
const digest = value => createHash('sha256').update(value).digest();
const reply = (res, status, body) => res.writeHead(status, { 'content-type': 'application/json', 'cache-control': 'no-store' }).end(JSON.stringify(body));
const fail = (res, status, code) => reply(res, status, { error: { message: code, code } });

export function validateText(body) {
  if (!body || typeof body !== 'object' || Array.isArray(body) || body.model !== 'openclaw/default' ||
    Object.keys(body).some(key => !['model', 'messages', 'temperature', 'max_tokens', 'stream'].includes(key)) ||
    (body.stream !== undefined && body.stream !== false) ||
    !Array.isArray(body.messages) || body.messages.length < 1 || body.messages.length > 100 ||
    body.messages.some(m => !m || !['system', 'user', 'assistant'].includes(m.role) || typeof m.content !== 'string' ||
      Object.keys(m).some(k => !['role', 'content'].includes(k))) ||
    !body.messages.some(m => m.role === 'user') ||
    (body.max_tokens !== undefined && (!Number.isInteger(body.max_tokens) || body.max_tokens < 1 || body.max_tokens > 2048)) ||
    (body.temperature !== undefined && (!Number.isFinite(body.temperature) || body.temperature < 0 || body.temperature > 2))) return null;
  return { model: 'openclaw/default', messages: body.messages.map(({ role, content }) => ({ role, content })), stream: false,
    max_tokens: body.max_tokens ?? 800, temperature: body.temperature ?? 0.2 };
}

// Do not expose the native gateway token or allow external callers to select tools,
// models, session keys, attachments, or other native routes. Only Draft's scoped
// server adapter should have the distinct ingress key.
export function createDraftText({ ingressKey, gatewayToken, gatewayPort, approved = false, fetchImpl = fetch }) {
  const enabled = approved && typeof ingressKey === 'string' && ingressKey.length >= 32 && ingressKey !== gatewayToken;
  const expected = enabled ? digest('Bearer ' + ingressKey) : null;
  let busy = false;
  return { enabled, async handle(req, res) {
    if (req.url !== route || req.method !== 'POST') return fail(res, 404, 'not_found');
    if (!expected) return fail(res, 503, 'provider_unavailable');
    if (!timingSafeEqual(digest(String(req.headers.authorization || '')), expected)) return fail(res, 401, 'authentication_error');
    if (!String(req.headers['content-type'] || '').toLowerCase().startsWith('application/json')) return fail(res, 415, 'invalid_request_error');
    if (busy) return fail(res, 503, 'api_error');
    busy = true;
    try {
      const chunks = [];
      let size = 0;
      for await (const chunk of req) {
        size += chunk.length;
        if (size > limit) return fail(res, 413, 'invalid_request_error');
        chunks.push(chunk);
      }
      let body;
      try { body = validateText(JSON.parse(Buffer.concat(chunks).toString('utf8'))); } catch { /* invalid JSON */ }
      if (!body) return fail(res, 400, 'invalid_request_error');
      const response = await fetchImpl(`http://127.0.0.1:${gatewayPort}${route}`, {
        method: 'POST', redirect: 'error', signal: AbortSignal.timeout(22000),
        headers: { authorization: 'Bearer ' + gatewayToken, 'content-type': 'application/json' }, body: JSON.stringify(body)
      });
      const data = await response.json();
      if (!response.ok) {
        const codes = ['insufficient_quota', 'quota_exceeded', 'rate_limit_error', 'rate_limit_exceeded', 'subscription_sharing_usage_limit_exceeded', 'usage_limit_reached', 'too_many_requests', 'authentication_error', 'permission_error', 'api_error', 'server_error', 'invalid_request_error'];
        const code = data?.error?.code || data?.error?.type;
        return fail(res, response.status, codes.includes(code) ? code : 'upstream_error');
      }
      const choice = data?.choices?.[0];
      if (!choice || typeof choice.message?.content !== 'string' || !['stop', 'content_filter'].includes(choice.finish_reason) || choice.message.tool_calls?.length) return fail(res, 502, 'invalid_response');
      return reply(res, 200, { model: typeof data.model === 'string' ? data.model : 'openclaw/default', choices: [{ index: 0, message: { role: 'assistant', content: choice.message.content }, finish_reason: choice.finish_reason }] });
    } catch (error) { return fail(res, error?.name === 'TimeoutError' ? 504 : 503, 'api_error'); }
    finally { busy = false; }
  } };
}
