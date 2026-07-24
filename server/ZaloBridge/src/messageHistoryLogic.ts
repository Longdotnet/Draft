import type { BridgeHistoricalMessage, BridgeMessageHistoryProbe } from "./contracts.js";
import { normalizeId, normalizeMemberId } from "./pollLogic.js";

export type RawHistoricalMessage = {
  isSelf?: boolean;
  data?: {
    actionId?: string;
    msgId?: string;
    cliMsgId?: string;
    msgType?: string;
    uidFrom?: string;
    dName?: string;
    ts?: string | number;
    content?: unknown;
  };
};

export function normalizeHistoricalMessage(
  value: RawHistoricalMessage,
): BridgeHistoricalMessage | null {
  const data = value.data ?? {};
  const messageId = normalizeId(data.msgId ?? data.cliMsgId ?? data.actionId ?? "");
  const senderId = normalizeMemberId(data.uidFrom ?? "");
  if (!messageId || !senderId) return null;
  return {
    messageId,
    senderId,
    senderName: String(data.dName || `Zalo ${senderId}`),
    content: typeof data.content === "string" ? data.content : "",
    messageType: String(data.msgType ?? "unknown"),
    isFromBot: Boolean(value.isSelf),
    sentAtUnixMs: normalizeUnixMs(data.ts),
  };
}

export function buildMessageHistoryProbe(
  groupId: string,
  requestedCount: number,
  messages: BridgeHistoricalMessage[],
  more: number,
  lastActionId: string | null,
  lastActionIdOther: string | null,
): BridgeMessageHistoryProbe {
  const timestamps = messages
    .map((message) => message.sentAtUnixMs)
    .filter((value) => value > 0);
  return {
    groupId,
    requestedCount,
    returnedCount: messages.length,
    more,
    lastActionId,
    lastActionIdOther,
    oldestMessageAtUnixMs: timestamps.length > 0 ? Math.min(...timestamps) : null,
    newestMessageAtUnixMs: timestamps.length > 0 ? Math.max(...timestamps) : null,
    messages,
  };
}

export function normalizeUnixMs(value: unknown): number {
  const parsed = Number(value ?? 0);
  if (!Number.isFinite(parsed) || parsed <= 0) return 0;
  return parsed < 10_000_000_000 ? parsed * 1000 : parsed;
}
