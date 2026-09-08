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

/**
 * Buffers inbound Zalo listener events while the VolleyDraft API is temporarily
 * unavailable. Deliveries are serialized per account + endpoint so conversation order
 * is preserved without allowing one sleeping API target to block another account.
 *
 * This queue never calls Zalo. If the bridge process itself restarts, listener-generation
 * recovery in the API remains the durable fallback for the gap across that restart.
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
  private readonly inFlightIds = new Set<string>();
  private readonly stats: WebhookDeliveryStats = {
    pending: 0,
    accepted: 0,
    delivered: 0,
    retryAttempts: 0,
    permanentFailures: 0,
    expired: 0,
    overflowRejected: 0,
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

    if (this.inFlightIds.has(deliveryId)) return Promise.resolve();

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

    this.inFlightIds.add(deliveryId);
    this.pendingByAccount.set(accountId, pending + 1);
    this.stats.pending += 1;
    this.stats.accepted += 1;
    const serialKey = `${accountId}:${request.url}`;

    return this.serial.run(serialKey, () => this.deliverWithRetry(request))
      .finally(() => {
        this.inFlightIds.delete(deliveryId);
        const remaining = Math.max(0, (this.pendingByAccount.get(accountId) ?? 1) - 1);
        if (remaining === 0) this.pendingByAccount.delete(accountId);
        else this.pendingByAccount.set(accountId, remaining);
        this.stats.pending = Math.max(0, this.stats.pending - 1);
      });
  }

  snapshot(): WebhookDeliveryStats {
    return { ...this.stats };
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
