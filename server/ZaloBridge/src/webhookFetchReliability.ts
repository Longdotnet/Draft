import { createHash } from "node:crypto";
import { webhookDeliveryQueue, type WebhookDeliveryKind } from "./webhookDeliveryQueue.js";

const originalFetch = globalThis.fetch.bind(globalThis);

function headerValue(headers: HeadersInit | undefined, name: string): string | null {
  if (!headers) return null;
  return new Headers(headers).get(name)?.trim() || null;
}

function eventIdentity(body: Record<string, unknown>): { accountId: string; kind: WebhookDeliveryKind; deliveryId: string } | null {
  const accountId = String(body.accountId ?? "").trim();
  if (!accountId) return null;

  const messageId = String(body.messageId ?? "").trim();
  if (messageId) {
    return {
      accountId,
      kind: "message",
      deliveryId: `message:${accountId}:${messageId}`,
    };
  }

  const groupId = String(body.groupId ?? "").trim();
  const eventType = String(body.eventType ?? "").trim();
  if (!groupId || !eventType) return null;
  const stable = createHash("sha256")
    .update(JSON.stringify({
      accountId,
      groupId,
      eventType,
      boardId: body.boardId ?? null,
      occurredAtUnixMs: body.occurredAtUnixMs ?? null,
    }))
    .digest("hex")
    .slice(0, 24);
  return {
    accountId,
    kind: "poll",
    deliveryId: `poll:${accountId}:${groupId}:${stable}`,
  };
}

function requestUrl(input: string | URL | Request): string {
  if (input instanceof Request) return input.url;
  return String(input);
}

async function reliableFetch(input: string | URL | Request, init?: RequestInit): Promise<Response> {
  const method = String(init?.method ?? (input instanceof Request ? input.method : "GET")).toUpperCase();
  const webhookKey = headerValue(init?.headers ?? (input instanceof Request ? input.headers : undefined), "x-zalo-bridge-key");
  if (method !== "POST" || !webhookKey || typeof init?.body !== "string") {
    return originalFetch(input, init);
  }

  let body: Record<string, unknown> | null = null;
  try {
    const parsed = JSON.parse(init.body) as unknown;
    if (parsed && typeof parsed === "object" && !Array.isArray(parsed)) body = parsed as Record<string, unknown>;
  } catch {
    return originalFetch(input, init);
  }

  const identity = body ? eventIdentity(body) : null;
  if (!identity) return originalFetch(input, init);

  await webhookDeliveryQueue.enqueue({
    ...identity,
    url: requestUrl(input),
    webhookKey,
    body,
  });

  // The caller only needs an ok response. The actual API response has already been
  // consumed by the delivery queue after a confirmed 2xx, so return a local success.
  return new Response(null, {
    status: 204,
    headers: { "x-zalo-bridge-buffered-delivery": "1" },
  });
}

if (globalThis.fetch !== reliableFetch) {
  globalThis.fetch = reliableFetch as typeof fetch;
}

export function getWebhookDeliveryStats() {
  return webhookDeliveryQueue.snapshot();
}
