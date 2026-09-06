import { createHash } from "node:crypto";

export type ScopedIdempotencyEntry<T> = {
  expiresAt: number;
  payloadFingerprint: string;
  result: Promise<T>;
};

export class ScopedOutboundIdempotency<T> {
  private readonly entries = new Map<string, ScopedIdempotencyEntry<T>>();

  constructor(private readonly ttlMs = 24 * 60 * 60 * 1000) {}

  run(
    scope: { accountId: string; groupId: string; idempotencyKey?: string | null },
    payload: unknown,
    factory: () => Promise<T>,
    now = Date.now(),
  ): Promise<T> {
    const idempotencyKey = scope.idempotencyKey?.trim();
    if (!idempotencyKey) return factory();

    this.prune(now);
    const scopedKey = `${normalize(scope.accountId)}:${normalize(scope.groupId)}:${idempotencyKey}`;
    const payloadFingerprint = fingerprintPayload(payload);
    const existing = this.entries.get(scopedKey);
    if (existing && existing.expiresAt > now) {
      if (existing.payloadFingerprint !== payloadFingerprint) {
        throw new Error("Idempotency key was reused with a different outbound payload");
      }
      return existing.result;
    }

    const result = factory().catch((error) => {
      const current = this.entries.get(scopedKey);
      if (current?.result === result) this.entries.delete(scopedKey);
      throw error;
    });
    this.entries.set(scopedKey, {
      expiresAt: now + this.ttlMs,
      payloadFingerprint,
      result,
    });
    return result;
  }

  private prune(now: number) {
    for (const [key, entry] of this.entries) {
      if (entry.expiresAt <= now) this.entries.delete(key);
    }
  }
}

export function fingerprintPayload(payload: unknown): string {
  return createHash("sha256").update(stableSerialize(payload)).digest("hex");
}

function normalize(value: string): string {
  return value.trim();
}

function stableSerialize(value: unknown): string {
  if (value === null || typeof value !== "object") return JSON.stringify(value);
  if (Array.isArray(value)) return `[${value.map(stableSerialize).join(",")}]`;
  const record = value as Record<string, unknown>;
  return `{${Object.keys(record)
    .sort()
    .map((key) => `${JSON.stringify(key)}:${stableSerialize(record[key])}`)
    .join(",")}}`;
}
