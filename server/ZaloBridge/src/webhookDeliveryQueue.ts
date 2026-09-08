import { createHash } from "node:crypto";
import { KeyedSerialExecutor } from "./keyedSerialExecutor.js";

export type WebhookDeliveryKind = "message" | "poll";

export type WebhookDeliveryRequest = {
  deliveryId: string;
  accountId: string;
  kind: WebhookDeliveryKind;
  url: string;
  webhookKey: string;
  body: unknown;
};

type DeliveryResult = {
  ok: boolean;
  status: number | null;
  retryable: boolean;
  retryAfterMs: number | null;
  error: string | null;
};

type WebhookDeliveryQueueOptions = {
  fetchImpl?: typeof fetch;
  sleep?: (milliseconds: number) => Promise<void>;
  now?: () => number;
  random?: () => number;
  baseDelayMs?: number;
  maxDelayMs?: number;
  maxRetryAfterMs?: number;
  maxDeliveryAgeMs?: number;
  maxPendingPerAccount?: number;
  requestTimeoutMs?: number;
  onLog?: (event: Record<string, unknown>) => void;
};

export type WebhookDeliveryStats = {
  pending: number;
  accepted: number;
  delivered: number;
  retryAttempts: number;
  permanentFailures: number;
  expired: number;
  overflowRejected: number;
  coalescedDuplicates: number;
  idempotencyConflicts: number;
};

type InFlightDelivery = {
  signature: string;
  result: Promise<void>;
};

const defaultSleep = (milliseconds: number) =>
  new Promise<void>((resolve) => setTimeout(resolve, milliseconds));

function retryAfterMs(response: Response, nowUnixMs: number, maxRetryAfterMs: number): number | null {
  const raw = response.headers.get("retry-after")?.trim();
  if (!raw) return null;

  const seconds = Number(raw);
  if (Number.isFinite(seconds) && seconds >= 0) {
    return Math.min(maxRetryAfterMs, Math.max(1_000, Math.ceil(seconds * 1_000)));
  }

  const retryAt = Date.parse(raw);
  if (!Number.isFinite(retryAt)) return null;
  return Math.min(maxRetryAfterMs, Math.max(1_000, retryAt - nowUnixMs));
}

function retryableStatus(status: number): boolean {
  return status === 408 || status === 425 || status === 429 || status >= 500;
}

function deliverySignature(request: WebhookDeliveryRequest): string {
  // The webhook key is deliberately excluded. It is transport authentication, not part
  // of the event identity, and may rotate while the same accepted event is still being
  // delivered. Account/kind/url/body are bound so a reused delivery ID cannot silently
  // acknowledge a materially different side effect.
  return createHash("sha256")
    .update(JSON.stringify({
      accountId: request.accountId.trim(),
      kind: request.kind,
      url: request.url,
      body: request.body,
    }))
    .digest("hex");
}

/**
 * Buffers inbound Zalo listener events while the VolleyDraft API is temporarily
 * unavailable. Deliveries are serialized per account + endpoint so conversation order
 * is preserved without allowing one sleeping API target to block another account.
 *
 * Duplicate delivery IDs are true single-flight operations: an equivalent duplicate
 * receives the exact same promise/outcome as the original attempt. A conflicting reuse
 * fails closed instead of reporting local success for a different event. This matters
 * because the global fetch reliability shim returns success to the listener only after
 * this promise resolves.
 *
 * This queue never calls Zalo. Planned bridge shutdowns can drain accepted deliveries
 * before the process exits. A hard process/container loss can still lose memory-only
 * work, so listener-generation recovery remains the fallback where provider history is
 * available.
 */
export class WebhookDeliveryQueue {
  private readonly serial = new KeyedSerialExecutor();
  private readonly fetchImpl: typeof fetch;
  private readonly sleep: (milliseconds: number) => Promise<void>;
  private readonly now: () => number;
  private readonly random: () => number;
  private readonly baseDelayMs: number;
  private readonly maxDelayMs: number;
  private readonly maxRetryAfterMs: number;
  private readonly maxDeliveryAgeMs: number;
  private readonly maxPendingPerAccount: number;
  private readonly requestTimeoutMs: number;
  private readonly onLog: (event: Record<string, unknown>) => void;
  private readonly pendingByAccount = new Map<string, number>();
  private readonly inFlightById = new Map<string, InFlightDelivery>();
  private readonly activeDeliveries = new Set<Promise<void>>();
  private readonly stats: WebhookDeliveryStats = {
    pending: 0,
    accepted: 0,
    delivered: 0,
    retryAttempts: 0,
    permanentFailures: 0,
    expired: 0,
    overflowRejected: 0,
    coalescedDuplicates: 0,
    idempotencyConflicts: 0,
  };

  constructor(options: WebhookDeliveryQueueOptions = {}) {
    this.fetchImpl = options.fetchImpl ?? fetch;
    this.sleep = options.sleep ?? defaultSleep;
    this.now = options.now ?? Date.now;
    this.random = options.random ?? Math.random;
    this.baseDelayMs = Math.max(100, options.baseDelayMs ?? 2_000);
    this.maxDelayMs = Math.max(this.baseDelayMs, options.maxDelayMs ?? 60_000);
    this.maxRetryAfterMs = Math.max(1_000, options.maxRetryAfterMs ?? 5 * 60_000);
    this.maxDeliveryAgeMs = Math.max(this.baseDelayMs, options.maxDeliveryAgeMs ?? 30 * 60_000);
    this.maxPendingPerAccount = Math.max(1, options.maxPendingPerAccount ?? 500);
    this.requestTimeoutMs = Math.max(1_000, options.requestTimeoutMs ?? 45_000);
    this.onLog = options.onLog ?? ((event) => console.info("[Zalo webhook delivery]", event));
  }

  enqueue(request: WebhookDeliveryRequest): Promise<void> {
    const accountId = request.accountId.trim();
    const deliveryId = request.deliveryId.trim();
    if (!accountId || !deliveryId) {
      return Promise.reject(new Error("Webhook delivery requires accountId and deliveryId"));
    }

    const signature = deliverySignature(request);
    const existing = this.inFlightById.get(deliveryId);
    if (existing) {
      if (existing.signature !== signature) {
        this.stats.idempotencyConflicts += 1;
        this.onLog({
          outcome: "idempotency_conflict",
          accountId,
          kind: request.kind,
        });
        return Promise.reject(new Error("Webhook delivery id conflicts with an in-flight payload"));
      }
      this.stats.coalescedDuplicates += 1;
      return existing.result;
    }

    const pending = this.pendingByAccount.get(accountId) ?? 0;
    if (pending >= this.maxPendingPerAccount) {
      this.stats.overflowRejected += 1;
      this.onLog({
        outcome: "overflow_rejected",
        accountId,
        kind: request.kind,
        pending,
      });
      return Promise.reject(new Error(`Webhook delivery backlog exceeded ${this.maxPendingPerAccount} events for account`));
    }

    this.pendingByAccount.set(accountId, pending + 1);
    this.stats.pending += 1;
    this.stats.accepted += 1;
    const serialKey = JSON.stringify([accountId, request.url]);

    const delivery = this.serial.run(serialKey, () => this.deliverWithRetry(request))
      .finally(() => {
        const current = this.inFlightById.get(deliveryId);
        if (current?.result === delivery) this.inFlightById.delete(deliveryId);
        const remaining = Math.max(0, (this.pendingByAccount.get(accountId) ?? 1) - 1);
        if (remaining === 0) this.pendingByAccount.delete(accountId);
        else this.pendingByAccount.set(accountId, remaining);
        this.stats.pending = Math.max(0, this.stats.pending - 1);
      });

    this.inFlightById.set(deliveryId, { signature, result: delivery });
    this.activeDeliveries.add(delivery);
    void delivery.then(
      () => this.activeDeliveries.delete(delivery),
      () => this.activeDeliveries.delete(delivery),
    );
    return delivery;
  }

  snapshot(): WebhookDeliveryStats {
    return { ...this.stats };
  }

  /**
   * Waits for deliveries already accepted by the queue to settle, bounded by the host's
   * shutdown grace period. Callers should stop listener ingress before invoking this so
   * the active set cannot grow indefinitely while shutdown is in progress.
   */
  async drain(maxWaitMs: number): Promise<boolean> {
    const timeoutMs = Math.max(0, Math.trunc(maxWaitMs));
    const active = [...this.activeDeliveries];
    if (active.length === 0) return true;
    if (timeoutMs === 0) return false;

    let timer: ReturnType<typeof setTimeout> | undefined;
    try {
      return await Promise.race([
        Promise.allSettled(active).then(() => true),
        new Promise<boolean>((resolve) => {
          timer = setTimeout(() => resolve(false), timeoutMs);
        }),
      ]);
    } finally {
      if (timer) clearTimeout(timer);
    }
  }

  private async deliverWithRetry(request: WebhookDeliveryRequest): Promise<void> {
    const startedAt = this.now();
    let attempt = 0;

    while (true) {
      attempt += 1;
      const result = await this.tryDeliver(request);
      if (result.ok) {
        this.stats.delivered += 1;
        if (attempt > 1) {
          this.onLog({
            outcome: "recovered",
            accountId: request.accountId,
            kind: request.kind,
            attempts: attempt,
          });
        }
        return;
      }

      if (!result.retryable) {
        this.stats.permanentFailures += 1;
        this.onLog({
          outcome: "permanent_failure",
          accountId: request.accountId,
          kind: request.kind,
          attempts: attempt,
          status: result.status,
          error: result.error,
        });
        throw new Error(result.error ?? `Webhook delivery failed with HTTP ${result.status}`);
      }

      const ageMs = this.now() - startedAt;
      if (ageMs >= this.maxDeliveryAgeMs) {
        this.stats.expired += 1;
        this.onLog({
          outcome: "expired",
          accountId: request.accountId,
          kind: request.kind,
          attempts: attempt,
          status: result.status,
          ageMs,
        });
        throw new Error(`Webhook delivery remained unavailable for ${ageMs}ms`);
      }

      this.stats.retryAttempts += 1;
      const exponential = Math.min(this.maxDelayMs, this.baseDelayMs * 2 ** Math.min(8, attempt - 1));
      const jitter = Math.floor(this.random() * Math.min(1_000, Math.max(1, exponential / 4)));
      const delayMs = Math.max(exponential + jitter, result.retryAfterMs ?? 0);
      const remainingAgeMs = Math.max(0, this.maxDeliveryAgeMs - ageMs);
      await this.sleep(Math.min(delayMs, remainingAgeMs));
    }
  }

  private async tryDeliver(request: WebhookDeliveryRequest): Promise<DeliveryResult> {
    try {
      const response = await this.fetchImpl(request.url, {
        method: "POST",
        headers: {
          "content-type": "application/json",
          "x-zalo-bridge-key": request.webhookKey,
          "x-zalo-bridge-delivery-id": request.deliveryId,
        },
        body: JSON.stringify(request.body),
        signal: AbortSignal.timeout(this.requestTimeoutMs),
      });
      if (response.ok) {
        return { ok: true, status: response.status, retryable: false, retryAfterMs: null, error: null };
      }

      let body = "";
      try {
        body = (await response.text()).slice(0, 300);
      } catch {
        // Response body is diagnostics only.
      }
      return {
        ok: false,
        status: response.status,
        retryable: retryableStatus(response.status),
        retryAfterMs: retryAfterMs(response, this.now(), this.maxRetryAfterMs),
        error: `VolleyDraft webhook returned ${response.status}${body ? `: ${body}` : ""}`,
      };
    } catch (error) {
      return {
        ok: false,
        status: null,
        retryable: true,
        retryAfterMs: null,
        error: error instanceof Error ? error.message : "Webhook network failure",
      };
    }
  }
}

export const webhookDeliveryQueue = new WebhookDeliveryQueue();
