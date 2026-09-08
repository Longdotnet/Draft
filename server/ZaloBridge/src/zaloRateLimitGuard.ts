import { BridgeHttpError, classifyBridgeError, upstreamRetryAfterSeconds } from "./bridgeErrors.js";

type AccountRateState = {
  blockedUntil: number;
  consecutiveRateLimits: number;
  nextAllowedAt: number;
  tail: Promise<void>;
};

type ZaloRateLimitGuardOptions = {
  minGapMs?: number;
  defaultCooldownMs?: number;
  maxCooldownMs?: number;
  now?: () => number;
  sleep?: (milliseconds: number) => Promise<void>;
};

const defaultSleep = (milliseconds: number) =>
  new Promise<void>((resolve) => setTimeout(resolve, milliseconds));

/**
 * Account-scoped protection for outbound Zalo side effects.
 *
 * This deliberately never retries an upstream failure. It serializes sends for one
 * connected account, keeps a small gap between provider calls, and opens a local
 * cooldown after an upstream 429 so concurrent callers cannot keep hammering Zalo.
 */
export class ZaloRateLimitGuard {
  private readonly states = new Map<string, AccountRateState>();
  private readonly minGapMs: number;
  private readonly defaultCooldownMs: number;
  private readonly maxCooldownMs: number;
  private readonly now: () => number;
  private readonly sleep: (milliseconds: number) => Promise<void>;

  constructor(options: ZaloRateLimitGuardOptions = {}) {
    this.minGapMs = Math.max(0, options.minGapMs ?? 750);
    this.defaultCooldownMs = Math.max(1_000, options.defaultCooldownMs ?? 60_000);
    this.maxCooldownMs = Math.max(this.defaultCooldownMs, options.maxCooldownMs ?? 15 * 60_000);
    this.now = options.now ?? Date.now;
    this.sleep = options.sleep ?? defaultSleep;
  }

  async run<T>(accountId: string, operation: () => Promise<T>): Promise<T> {
    const key = accountId.trim();
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
        await this.sleep(state.nextAllowedAt - startedAt);
      }

      try {
        const result = await operation();
        state.consecutiveRateLimits = 0;
        state.nextAllowedAt = this.now() + this.minGapMs;
        return result;
      } catch (error) {
        const descriptor = classifyBridgeError(error);
        if (descriptor.source === "upstream-zalo" && descriptor.kind === "rate_limited") {
          state.consecutiveRateLimits += 1;
          const exponential = this.defaultCooldownMs * 2 ** Math.min(4, state.consecutiveRateLimits - 1);
          const retryAfterMs = (upstreamRetryAfterSeconds(error, this.now()) ?? 0) * 1_000;
          const cooldownMs = Math.min(this.maxCooldownMs, Math.max(exponential, retryAfterMs));
          state.blockedUntil = this.now() + cooldownMs;
          state.nextAllowedAt = state.blockedUntil;
        }
        throw error;
      }
    } finally {
      release();
    }
  }

  private stateFor(accountId: string): AccountRateState {
    const existing = this.states.get(accountId);
    if (existing) return existing;

    const created: AccountRateState = {
      blockedUntil: 0,
      consecutiveRateLimits: 0,
      nextAllowedAt: 0,
      tail: Promise.resolve(),
    };
    this.states.set(accountId, created);
    return created;
  }
}
