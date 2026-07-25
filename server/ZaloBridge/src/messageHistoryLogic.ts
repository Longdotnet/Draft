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
  isSupported = true,
  limitationCode: string | null = null,
): BridgeMessageHistoryProbe {
  const timestamps = messages
    .map((message) => message.sentAtUnixMs)
    .filter((value) => value > 0);
  return {
    groupId,
    requestedCount,
    isSupported,
    limitationCode,
    returnedCount: messages.length,
    more,
    lastActionId,
    lastActionIdOther,
    oldestMessageAtUnixMs: timestamps.length > 0 ? Math.min(...timestamps) : null,
    newestMessageAtUnixMs: timestamps.length > 0 ? Math.max(...timestamps) : null,
    messages,
  };
}

export function buildUnsupportedMessageHistoryProbe(
  groupId: string,
  requestedCount: number,
  limitationCode: string,
): BridgeMessageHistoryProbe {
  return buildMessageHistoryProbe(
    groupId,
    requestedCount,
    [],
    0,
    null,
    null,
    false,
    limitationCode,
  );
}

export function isHistoryEndpointUnavailable(error: unknown): boolean {
  if (!error || typeof error !== "object") return false;
  const candidate = error as {
    message?: unknown;
    status?: unknown;
    statusCode?: unknown;
    response?: { status?: unknown };
  };
  const status = Number(candidate.response?.status ?? candidate.statusCode ?? candidate.status ?? 0);
  if (status === 404) return true;
  const message = String(candidate.message ?? "");
  return /(?:status(?:\s+code)?|http)\s*404\b/i.test(message);
}

export function normalizeUnixMs(value: unknown): number {
  const parsed = Number(value ?? 0);
  if (!Number.isFinite(parsed) || parsed <= 0) return 0;
  return parsed < 10_000_000_000 ? parsed * 1000 : parsed;
}
