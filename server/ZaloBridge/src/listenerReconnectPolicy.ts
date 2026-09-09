export type ListenerCloseDisposition =
  | "manual"
  | "duplicate_connection"
  | "kicked_or_auth"
  | "transient";

const reconnectBackoffMs = [2_000, 5_000, 10_000, 20_000, 30_000] as const;

export function classifyListenerClose(code: number): ListenerCloseDisposition {
  if (code === 1000) return "manual";
  if (code === 3000) return "duplicate_connection";
  if (code === 3003) return "kicked_or_auth";
  return "transient";
}

export function listenerReconnectDelayMs(
  attempt: number,
  random: () => number = Math.random,
): number | null {
  if (!Number.isInteger(attempt) || attempt <= 0) {
    throw new RangeError("Reconnect attempt must be a positive integer");
  }
  if (attempt > reconnectBackoffMs.length) return null;

  const base = reconnectBackoffMs[attempt - 1];
  const boundedRandom = Math.min(1, Math.max(0, random()));
  const jitter = 0.8 + boundedRandom * 0.4;
  return Math.round(base * jitter);
}

export function listenerReconnectAttemptLimit(): number {
  return reconnectBackoffMs.length;
}
