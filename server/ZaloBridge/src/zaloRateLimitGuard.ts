import { BridgeHttpError, classifyBridgeError, upstreamRetryAfterSeconds } from "./bridgeErrors.js";

type AccountRateState = {
  blockedUntil: number;
  consecutiveRateLimits: number;
  nextAllowedAt: number;
  tail: Promise<void>;
};

export type ZaloRateLimitEventType =
  | "cooldown_rejected"
  | "pacing_wait"
  | "provider_attempt"
  | "provider_success"
  | "provider_failure";

export type ZaloRateLimitEvent = {
  type: ZaloRateLimitEventType;
  scopeKey: string;
  operation: string;
  atUnixMs: number;
  providerTouched: boolean;
  waitMs?: number;
  blockedUntilUnixMs?: number;
  retryAfterSeconds?: number;
  errorSource?: string;
  errorKind?: string;
  status?: number;
};

export type ZaloRateLimitGuardOptions = {
  minGapMs?: number;
  defaultCooldownMs?: number;
  maxCooldownMs?: number;
  maxRetryAfterMs?: number;
  now?: () => number;
  sleep?: (milliseconds: number) => Promise<void>;
  onEvent?: (event: ZaloRateLimitEvent) => void;
};

const defaultSleep = (milliseconds: number) =>
  new Promise<void>((resolve) => setTimeout(resolve, milliseconds));

/**
 * Scoped protection for Zalo provider traffic.
 *
 * The caller owns the scope key. A connected account should route every relevant read
 * and write through the same scope so a 429 from one capability can cool down the rest.
 * The guard deliberately never retries an upstream failure. It serializes work for one
 * scope, keeps a small gap between provider attempts regardless of outcome, and opens a
 * local cooldown after an upstream 429 so concurrent callers cannot keep hammering Zalo.
 *
 * `maxCooldownMs` caps only bridge-generated exponential backoff. A real Retry-After is
 * provider authority and must not be shortened to that local cap; otherwise a one-hour
 * provider directive could become a new request after fifteen minutes. Retry-After is
 * separately bounded by `maxRetryAfterMs` to protect the process from malformed dates or
 * absurd values while still allowing long provider-requested quiet periods.
 *
 * Observability events deliberately distinguish local rejection from a real provider
 * attempt. They contain only a caller-supplied operation label and internal scope key;
 * callers must not expose the scope key because credential-backed keys are hashed but
 * still unnecessary operational identifiers.
 */
export class ZaloRateLimitGuard {
  private readonly states = new Map<string, AccountRateState>();
  private readonly minGapMs: number;
  private readonly defaultCooldownMs: number;
  private readonly maxCooldownMs: number;
  private readonly maxRetryAfterMs: number;
  private readonly now: () => number;
  private readonly sleep: (milliseconds: number) => Promise<void>;
  private readonly onEvent?: (event: ZaloRateLimitEvent) => void;

  constructor(options: ZaloRateLimitGuardOptions = {}) {
    this.minGapMs = Math.max(0, options.minGapMs ?? 750);
    this.defaultCooldownMs = Math.max(1_000, options.defaultCooldownMs ?? 60_000);
    this.maxCooldownMs = Math.max(this.defaultCooldownMs, options.maxCooldownMs ?? 15 * 60_000);
    this.maxRetryAfterMs = Math.max(
      this.maxCooldownMs,
      options.maxRetryAfterMs ?? 7 * 24 * 60 * 60_000,
    );
    this.now = options.now ?? Date.now;
    this.sleep = options.sleep ?? defaultSleep;
    this.onEvent = options.onEvent;
  }

  async run<T>(scopeKey: string, operation: () => Promise<T>, operationName = "provider.call"): Promise<T> {
    const key = scopeKey.trim();
    const normalizedOperation = operationName.trim() || "provider.call";
    if (!key) return operation();

    const state = this.stateFor(key);
    const previous = state.tail;
    let release!: () => void;
    const turn = new Promise<void>((resolve) => {
      release = resolve;
    });
    state.tail = previous.then(() => turn, () => turn);

    await previous.catch(() => undefined);
    try {
      const startedAt = this.now();
      if (state.blockedUntil > startedAt) {
        const retryAfterSeconds = Math.max(1, Math.ceil((state.blockedUntil - startedAt) / 1_000));
        this.emit({
          type: "cooldown_rejected",
          scopeKey: key,
          operation: normalizedOperation,
          atUnixMs: startedAt,
          providerTouched: false,
          blockedUntilUnixMs: state.blockedUntil,
          retryAfterSeconds,
        });
        throw new BridgeHttpError(
          429,
          "upstream-zalo",
          "rate_limit_cooldown",
          true,
          "Zalo upstream request is cooling down after a rate limit.",
          retryAfterSeconds,
        );
      }

      if (state.nextAllowedAt > startedAt) {
        const waitMs = state.nextAllowedAt - startedAt;
        this.emit({
          type: "pacing_wait",
          scopeKey: key,
          operation: normalizedOperation,
          atUnixMs: startedAt,
          providerTouched: false,
          waitMs,
        });
        await this.sleep(waitMs);
      }

      const attemptAt = this.now();
      this.emit({
        type: "provider_attempt",
        scopeKey: key,
        operation: normalizedOperation,
        atUnixMs: attemptAt,
        providerTouched: true,
      });

      try {
        const result = await operation();
        state.consecutiveRateLimits = 0;
        this.emit({
          type: "provider_success",
          scopeKey: key,
          operation: normalizedOperation,
          atUnixMs: this.now(),
          providerTouched: true,
        });
        return result;
      } catch (error) {
        const descriptor = classifyBridgeError(error);
        let retryAfterSeconds: number | undefined;
        if (descriptor.source === "upstream-zalo" && descriptor.kind === "rate_limited") {
          state.consecutiveRateLimits += 1;
          const exponential = this.defaultCooldownMs * 2 ** Math.min(4, state.consecutiveRateLimits - 1);
          const localCooldownMs = Math.min(this.maxCooldownMs, exponential);
          const upstreamRetryAfter = upstreamRetryAfterSeconds(error, this.now());
          const upstreamCooldownMs = Math.min(
            this.maxRetryAfterMs,
            Math.max(0, (upstreamRetryAfter ?? 0) * 1_000),
          );
          const cooldownMs = Math.max(localCooldownMs, upstreamCooldownMs);
          state.blockedUntil = this.now() + cooldownMs;
          state.nextAllowedAt = state.blockedUntil;
          retryAfterSeconds = Math.max(1, Math.ceil(cooldownMs / 1_000));
        }
        this.emit({
          type: "provider_failure",
          scopeKey: key,
          operation: normalizedOperation,
          atUnixMs: this.now(),
          providerTouched: true,
          errorSource: descriptor.source,
          errorKind: descriptor.kind,
          status: descriptor.status,
          ...(state.blockedUntil > this.now() ? { blockedUntilUnixMs: state.blockedUntil } : {}),
          ...(retryAfterSeconds === undefined ? {} : { retryAfterSeconds }),
        });
        throw error;
      } finally {
        // The spacing invariant applies to provider attempts, not only successful sends.
        // Without this, a queued request can immediately follow a 5xx/auth/network failure
        // and amplify an upstream incident even though work is serialized per scope.
        state.nextAllowedAt = Math.max(state.nextAllowedAt, this.now() + this.minGapMs);
      }
    } finally {
      release();
    }
  }

  private emit(event: ZaloRateLimitEvent): void {
    try {
      this.onEvent?.(event);
    } catch {
      // Telemetry is diagnostic only and must never alter provider traffic behavior.
    }
  }

  private stateFor(scopeKey: string): AccountRateState {
    const existing = this.states.get(scopeKey);
    if (existing) return existing;

    const created: AccountRateState = {
      blockedUntil: 0,
      consecutiveRateLimits: 0,
      nextAllowedAt: 0,
      tail: Promise.resolve(),
    };
    this.states.set(scopeKey, created);
    return created;
  }
}
