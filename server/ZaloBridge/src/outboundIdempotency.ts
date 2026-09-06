import { createHash } from "node:crypto";

export type ScopedIdempotencyEntry<T> = {
  expiresAt: number | null;
  payloadFingerprint: string;
  result: Promise<T>;
  state: "pending" | "completed";
};

export class OutboundIdempotencyConflictError extends Error {
  constructor() {
    super("Idempotency key was reused with a different outbound payload");
    this.name = "OutboundIdempotencyConflictError";
  }
}

export class ScopedOutboundIdempotency<T> {
  private readonly entries = new Map<string, ScopedIdempotencyEntry<T>>();

  constructor(
    private readonly ttlMs = 24 * 60 * 60 * 1000,
    private readonly clock: () => number = Date.now,
  ) {}

  run(
    scope: { accountId: string; groupId: string; idempotencyKey?: string | null },
    payload: unknown,
    factory: () => Promise<T>,
    now = this.clock(),
  ): Promise<T> {
    const idempotencyKey = scope.idempotencyKey?.trim();
    if (!idempotencyKey) return factory();

    this.prune(now);
    const scopedKey = encodeScope(scope.accountId, scope.groupId, idempotencyKey);
    const payloadFingerprint = fingerprintPayload(payload);
    const existing = this.entries.get(scopedKey);
    if (existing && (existing.state === "pending" || (existing.expiresAt ?? 0) > now)) {
      if (existing.payloadFingerprint !== payloadFingerprint) {
        throw new OutboundIdempotencyConflictError();
      }
      return existing.result;
    }

    // Pending provider sends must never age out. Expiring an in-flight entry can let a
    // retry execute the same side effect concurrently while the original request is
    // merely slow. Start the replay TTL only after the provider call completes.
    let result!: Promise<T>;
    result = Promise.resolve()
      .then(factory)
      .then((value) => {
        const current = this.entries.get(scopedKey);
        if (current?.result === result) {
          current.state = "completed";
          current.expiresAt = this.clock() + this.ttlMs;
        }
        return value;
      })
      .catch((error) => {
        const current = this.entries.get(scopedKey);
        if (current?.result === result) this.entries.delete(scopedKey);
        throw error;
      });

    this.entries.set(scopedKey, {
      expiresAt: null,
      payloadFingerprint,
      result,
      state: "pending",
    });
    return result;
  }

  private prune(now: number) {
    for (const [key, entry] of this.entries) {
      if (entry.state === "completed" && (entry.expiresAt ?? 0) <= now) {
        this.entries.delete(key);
      }
    }
  }
}

export function fingerprintPayload(payload: unknown): string {
  return createHash("sha256").update(stableSerialize(payload)).digest("hex");
}

function encodeScope(accountId: string, groupId: string, idempotencyKey: string): string {
  // Length/JSON framing avoids delimiter collisions if a future provider or caller key
  // contains punctuation such as ':'. Treat account, group and caller key as separate
  // identity fields rather than relying on a concatenated human-readable string.
  return JSON.stringify([normalize(accountId), normalize(groupId), idempotencyKey]);
}

function normalize(value: string): string {
  return value.trim();
}

function stableSerialize(value: unknown): string {
  if (value === null || typeof value !== "object") return JSON.stringify(value) ?? "undefined";
  if (Array.isArray(value)) return `[${value.map(stableSerialize).join(",")}]`;
  const record = value as Record<string, unknown>;
  return `{${Object.keys(record)
    .sort()
    .map((key) => `${JSON.stringify(key)}:${stableSerialize(record[key])}`)
    .join(",")}}`;
}
