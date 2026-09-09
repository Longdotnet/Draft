import { randomUUID } from "node:crypto";

export type InboundMessageReservation = {
  key: string;
  leaseId: string;
};

type MessageState =
  | { status: "pending"; leaseId: string }
  | { status: "delivered"; deliveredAt: number };

export type InboundMessageGateOptions = {
  now?: () => number;
  deliveredTtlMs?: number;
};

/**
 * Separates "currently being forwarded" from "confirmed delivered" for inbound Zalo
 * messages. A message is not remembered for the replay TTL until Bridge -> API delivery
 * succeeds. Failed delivery releases the reservation so a later listener replay can
 * recover the same provider message instead of being suppressed for 24 hours.
 */
export class InboundMessageDeliveryGate {
  private readonly states = new Map<string, MessageState>();
  private readonly now: () => number;
  private readonly deliveredTtlMs: number;

  constructor(options: InboundMessageGateOptions = {}) {
    this.now = options.now ?? Date.now;
    this.deliveredTtlMs = Math.max(1_000, options.deliveredTtlMs ?? 24 * 60 * 60_000);
  }

  reserve(accountIdValue: string, messageIdValue: string): InboundMessageReservation | null {
    const accountId = accountIdValue.trim();
    const messageId = messageIdValue.trim();
    if (!accountId || !messageId) return null;

    this.pruneDelivered();
    const key = JSON.stringify([accountId, messageId]);
    if (this.states.has(key)) return null;

    const leaseId = randomUUID();
    this.states.set(key, { status: "pending", leaseId });
    return { key, leaseId };
  }

  commit(reservation: InboundMessageReservation): boolean {
    const current = this.states.get(reservation.key);
    if (!current || current.status !== "pending" || current.leaseId !== reservation.leaseId) return false;
    this.states.set(reservation.key, { status: "delivered", deliveredAt: this.now() });
    return true;
  }

  release(reservation: InboundMessageReservation): boolean {
    const current = this.states.get(reservation.key);
    if (!current || current.status !== "pending" || current.leaseId !== reservation.leaseId) return false;
    this.states.delete(reservation.key);
    return true;
  }

  private pruneDelivered(): void {
    const cutoff = this.now() - this.deliveredTtlMs;
    for (const [key, state] of this.states) {
      if (state.status === "delivered" && state.deliveredAt <= cutoff) this.states.delete(key);
    }
  }
}

export function boardEventDebounceKey(input: {
  accountId: string;
  groupId: string;
  eventType: string;
  boardId?: string | null;
}): string {
  // Debounce repeated notifications for the same board transition only. Group-wide
  // debounce used to let two different polls updated within 1.5s overwrite one another.
  return JSON.stringify([
    input.accountId.trim(),
    input.groupId.trim(),
    input.eventType.trim(),
    input.boardId?.trim() || null,
  ]);
}
