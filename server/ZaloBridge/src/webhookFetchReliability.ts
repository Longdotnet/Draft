import { createHash } from "node:crypto";
import { webhookDeliveryQueue, type WebhookDeliveryKind } from "./webhookDeliveryQueue.js";

const originalFetch = globalThis.fetch.bind(globalThis);
const terminalFailureMemoMs = 10_000;

type DeliveryIdentity = {
  accountId: string;
  kind: WebhookDeliveryKind;
  deliveryId: string;
};

type QueueLike = {
  enqueue(input: {
    accountId: string;
    kind: WebhookDeliveryKind;
    deliveryId: string;
    url: string;
    webhookKey: string;
    body: Record<string, unknown>;
  }): Promise<void>;
};

type TerminalFailure = {
  signature: string;
  error: unknown;
  expiresAt: number;
};

export class WebhookTerminalFailureMemo {
  private readonly failures = new Map<string, TerminalFailure>();

  constructor(
    private readonly ttlMs = terminalFailureMemoMs,
    private readonly now: () => number = Date.now,
  ) {}

  get(deliveryId: string, signature: string): unknown | null {
    const entry = this.failures.get(deliveryId);
    if (!entry) return null;
    if (entry.expiresAt <= this.now()) {
      this.failures.delete(deliveryId);
      return null;
    }
    return entry.signature === signature ? entry.error : null;
  }

  remember(deliveryId: string, signature: string, error: unknown): void {
    this.failures.set(deliveryId, {
      signature,
      error,
      expiresAt: this.now() + this.ttlMs,
    });
  }

  clear(deliveryId: string): void {
    this.failures.delete(deliveryId);
  }
}

function headerValue(headers: HeadersInit | undefined, name: string): string | null {
  if (!headers) return null;
  return new Headers(headers).get(name)?.trim() || null;
}

function eventIdentity(body: Record<string, unknown>): DeliveryIdentity | null {
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

function deliverySignature(identity: DeliveryIdentity, url: string, body: Record<string, unknown>): string {
  return createHash("sha256")
    .update(JSON.stringify({
      accountId: identity.accountId,
      kind: identity.kind,
      deliveryId: identity.deliveryId,
      url,
      body,
    }))
    .digest("hex");
}

export function createReliableWebhookFetch(
  baseFetch: typeof fetch,
  queue: QueueLike,
  terminalFailures = new WebhookTerminalFailureMemo(),
): typeof fetch {
  return (async function reliableFetch(input: string | URL | Request, init?: RequestInit): Promise<Response> {
    const method = String(init?.method ?? (input instanceof Request ? input.method : "GET")).toUpperCase();
    const webhookKey = headerValue(init?.headers ?? (input instanceof Request ? input.headers : undefined), "x-zalo-bridge-key");
    if (method !== "POST" || !webhookKey || typeof init?.body !== "string") {
      return baseFetch(input, init);
    }

    let body: Record<string, unknown> | null = null;
    try {
      const parsed = JSON.parse(init.body) as unknown;
      if (parsed && typeof parsed === "object" && !Array.isArray(parsed)) body = parsed as Record<string, unknown>;
    } catch {
      return baseFetch(input, init);
    }

    const identity = body ? eventIdentity(body) : null;
    if (!identity) return baseFetch(input, init);

    const url = requestUrl(input);
    const signature = deliverySignature(identity, url, body!);
    const memoizedFailure = terminalFailures.get(identity.deliveryId, signature);
    if (memoizedFailure !== null) throw memoizedFailure;

    try {
      await queue.enqueue({
        ...identity,
        url,
        webhookKey,
        body: body!,
      });
      terminalFailures.clear(identity.deliveryId);
    } catch (error) {
      // The delivery queue already owns transient retry/backoff (including Retry-After).
      // Remember only the queue's final outcome long enough to absorb the legacy
      // postWebhook 1s/2s outer retries. This prevents one failed event from opening
      // multiple 30-minute retry cycles while still allowing a later listener replay.
      terminalFailures.remember(identity.deliveryId, signature, error);
      throw error;
    }

    // The caller only needs an ok response. The actual API response has already been
    // consumed by the delivery queue after a confirmed 2xx, so return a local success.
    return new Response(null, {
      status: 204,
      headers: { "x-zalo-bridge-buffered-delivery": "1" },
    });
  }) as typeof fetch;
}

const reliableFetch = createReliableWebhookFetch(originalFetch, webhookDeliveryQueue);
if (globalThis.fetch !== reliableFetch) {
  globalThis.fetch = reliableFetch;
}

export function getWebhookDeliveryStats() {
  return webhookDeliveryQueue.snapshot();
}

export function drainWebhookDeliveries(maxWaitMs: number): Promise<boolean> {
  return webhookDeliveryQueue.drain(maxWaitMs);
}
