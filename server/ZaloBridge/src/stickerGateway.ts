import { createHash } from "node:crypto";
import * as ZaloRuntime from "zca-js";
import type { SendGroupStickerRequest, ZaloCredentials } from "./contracts.js";
import { stickerKeywordsForReaction } from "./stickerLogic.js";

type BridgeStickerResult = {
  sent: boolean;
  mock: boolean;
  messageId: string | null;
};

type StickerDetail = {
  id: number;
  cateId: number;
  type: number;
};

type MinimalStickerApi = {
  getStickers(keyword: string): Promise<number[]>;
  getStickersDetail(stickerIds: number | number[]): Promise<StickerDetail[]>;
  sendSticker(
    sticker: StickerDetail,
    threadId: string,
    type: number,
  ): Promise<{ msgId?: number | string } | unknown>;
};

type MinimalStickerClient = {
  login(credentials: ZaloCredentials): Promise<MinimalStickerApi>;
};

const runtime = ZaloRuntime as unknown as {
  Zalo: new (options: Record<string, unknown>) => MinimalStickerClient;
  ThreadType: { Group: number };
};

const { Zalo, ThreadType } = runtime;
const mockMode = process.env.ZALO_BRIDGE_MOCK === "true";
const apiCache = new Map<string, { api: MinimalStickerApi; lastUsed: number }>();
const outgoingIdempotency = new Map<string, { expiresAt: number; result: Promise<BridgeStickerResult> }>();

function fingerprint(credentials: ZaloCredentials): string {
  return createHash("sha256").update(JSON.stringify(credentials)).digest("hex");
}

async function getApi(credentials: ZaloCredentials): Promise<MinimalStickerApi> {
  const key = fingerprint(credentials);
  const cached = apiCache.get(key);
  if (cached && Date.now() - cached.lastUsed < 10 * 60_000) {
    cached.lastUsed = Date.now();
    return cached.api;
  }

  const zalo = new Zalo({ logging: false, checkUpdate: false });
  const api = await zalo.login(credentials);
  apiCache.set(key, { api, lastUsed: Date.now() });
  return api;
}

function stableIndex(seed: string, count: number): number {
  if (count <= 1) return 0;
  const hash = createHash("sha256").update(seed).digest();
  return hash.readUInt32BE(0) % count;
}

function isSendableSticker(value: StickerDetail | null | undefined): value is StickerDetail {
  return Boolean(
    value &&
      Number.isFinite(value.id) && value.id > 0 &&
      Number.isFinite(value.cateId) && value.cateId >= 0 &&
      Number.isFinite(value.type) && value.type > 0,
  );
}

async function findSticker(api: MinimalStickerApi, request: SendGroupStickerRequest): Promise<StickerDetail> {
  for (const keyword of stickerKeywordsForReaction(request.reaction)) {
    // Keyword fallback is only for a successful provider lookup that returns no usable
    // sticker. A transport/provider exception is not evidence that another keyword will
    // work; continuing after 429/auth/5xx multiplies traffic exactly when Zalo is asking
    // the account to stop. Let the first provider failure escape to the account guard.
    const ids = await api.getStickers(keyword);
    if (!Array.isArray(ids) || ids.length === 0) continue;
    const seed = request.idempotencyKey || `${request.accountId}:${request.groupId}:${request.reaction}`;
    const stickerId = ids[stableIndex(seed, ids.length)];
    if (stickerId === undefined) continue;
    const details = await api.getStickersDetail(stickerId);
    const sticker = Array.isArray(details) ? details.find(isSendableSticker) : null;
    if (sticker) return sticker;
  }
  throw new Error(`No native Zalo sticker found for reaction ${request.reaction}`);
}

async function sendGroupStickerCore(request: SendGroupStickerRequest): Promise<BridgeStickerResult> {
  if (mockMode) {
    console.log(`[Zalo mock sticker -> ${request.groupId}] ${request.reaction}`);
    return { sent: true, mock: true, messageId: null };
  }

  const api = await getApi(request.credentials);
  const sticker = await findSticker(api, request);
  const result = await api.sendSticker(sticker, request.groupId, ThreadType.Group);
  const messageId = result && typeof result === "object" && "msgId" in result
    ? String((result as { msgId?: number | string }).msgId ?? "").trim() || null
    : null;
  return { sent: true, mock: false, messageId };
}

export async function sendGroupSticker(request: SendGroupStickerRequest): Promise<BridgeStickerResult> {
  const idempotencyKey = request.idempotencyKey?.trim();
  const now = Date.now();
  for (const [key, entry] of outgoingIdempotency) {
    if (entry.expiresAt <= now) outgoingIdempotency.delete(key);
  }

  if (!idempotencyKey) return sendGroupStickerCore(request);

  const existing = outgoingIdempotency.get(idempotencyKey);
  if (existing && existing.expiresAt > now) return existing.result;

  const result = sendGroupStickerCore(request).catch((error) => {
    outgoingIdempotency.delete(idempotencyKey);
    throw error;
  });
  outgoingIdempotency.set(idempotencyKey, {
    expiresAt: now + 24 * 60 * 60 * 1000,
    result,
  });
  return result;
}
